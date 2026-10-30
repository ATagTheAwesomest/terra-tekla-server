// A fake tekla-conversion-layer with a small made-up PowerFab job, for trying terra-tekla-server and
// the iPad app without the real PowerFab. It answers POST /api/powerfab/<Command> for every command
// the app's Shipping and Piece lookup use, in the same JSON shapes the real bridge returns (checked
// against live EPM-001 replies), and refuses what PowerFab refuses (loading more than is assigned,
// assigning more than the job has left, shipping a load with pieces not loaded).
//
//   node tools/fake-powerfab.mjs [port] [key]        (defaults 8099 and "fake-key")
//
// Then point the server at it: CONVERSION_URL=http://127.0.0.1:8099 CONVERSION_KEY=fake-key.
// Data lives in memory only and starts fresh each run. Never point a real server at this.

import http from 'node:http';

const port = Number(process.argv[2] ?? 8099);
const key = process.argv[3] ?? 'fake-key';

const JOB = { number: 'EPM-001', description: 'Test job', pcid: 13 };
// itemId, mark, sequenceId, sequence, total qty, weight kg each, shape, dimensions, grade, length mm
const ITEMS = [
  [8001, '1001-C1', 1, '1', 3, 478.5, 'W', '8 x 35', 'A992', 4200],
  [8002, '1002-B1', 1, '1', 2, 120.2, 'W', '12 x 26', 'A992', 6100],
  [8003, '1003-W2', 2, '2', 4, 35.0, 'L', '3 x 3 x 1/4', 'A36', 1200],
  [8004, '2001-C1', 2, '2', 1, 610.0, 'HSS', '6 x 6 x 1/2', 'A500-B', 5000],
  [8005, '1003-W2', 3, '3', 2, 35.0, 'L', '3 x 3 x 1/4', 'A36', 1200],
].map(([itemId, mark, sequenceId, sequence, total, kg, shape, dimensions, grade, length]) => ({ itemId, mark, sequenceId, sequence, total, kg, shape, dimensions, grade, length }));

let nextId = 1000;
const loads = [];  // { id, number, shipped, shipDate, capacity, material: [{ truckId, item, qty, loaded: [{id, qty}], returned: [{id, qty}] }] }

const sum = (list) => list.reduce((n, e) => n + e.qty, 0);
const assigned = (item) => loads.flatMap((l) => l.material).filter((m) => m.item === item).reduce((n, m) => n + m.qty, 0);

class Refused extends Error {}

function loadById(id) {
  const load = loads.find((l) => l.id === Number(id));
  if (!load) throw new Refused(`ProductionControlLoadID ${id} doesn't exist.`);
  return load;
}

function materialById(load, truckId) {
  const m = load.material.find((x) => x.truckId === Number(truckId));
  if (!m) throw new Refused(`ProductionControlItemTruckID ${truckId} isn't on load ${load.number}.`);
  return m;
}

function openLoad(load) {
  if (load.shipped) throw new Refused(`Load ${load.number} has been shipped.`);
}

const list = (v) => [v ?? []].flat().filter(Boolean);

function materialJson(m) {
  const i = m.item;
  return {
    ProductionControlItemTruckID: String(m.truckId),
    ProductionControlItemID: String(i.itemId),
    ProductionControlID: String(JOB.pcid),
    MainMarkDisplay: i.mark,
    PieceMarkDisplay: i.mark,
    SequenceDisplay: i.sequence,
    ShippingLength: String(i.length),
    ShippingWeightEach: String(i.kg),
    Quantity: String(m.qty),
    QuantityLoaded: String(sum(m.loaded)),
    QuantityReturned: String(sum(m.returned)),
    Shape: { '@ID': '1', '#text': i.shape },
    Dimensions: { '@Metric': '0', '#text': i.dimensions },
    Grade: { '@ID': '1', '#text': i.grade },
    Length: String(i.length),
    // One entry arrives as an object, several as a list, as from the real bridge.
    ...(m.loaded.length ? { Loaded: m.loaded.length === 1 ? { ID: String(m.loaded[0].id), Quantity: String(m.loaded[0].qty) } : m.loaded.map((e) => ({ ID: String(e.id), Quantity: String(e.qty) })) } : {}),
    ...(m.returned.length ? { Returned: m.returned.map((e) => ({ ID: String(e.id), Quantity: String(e.qty) })) } : {}),
  };
}

const today = () => new Date().toISOString().slice(0, 10);

const commands = {
  ProductionControlJob_Get: () => ({ ProductionControlJob: [{ JobNumber: JOB.number, JobDescription: JOB.description, JobLocation: 'Yard' }] }),
  GetProductionControlJobs: () => commands.ProductionControlJob_Get(),
  MaxTruck: (f) => {
    if (String(f.JobNumber).toUpperCase() !== JOB.number) throw new Refused(`Job ${f.JobNumber} not found.`);
    return { JobNumber: JOB.number, ProductionControlID: String(JOB.pcid), NextAvailableLoadNumber: String(loads.length + 1) };
  },
  GetAssemblies: () => ({
    Assembly: [...new Map(ITEMS.map((i) => [i.mark, i])).values()].map((i) => ({
      MainMark: i.mark, Weight: { '@UOM': 'kg', '#text': String(i.kg) },
      TotalQuantity: String(ITEMS.filter((x) => x.mark === i.mark).reduce((n, x) => n + x.total, 0)),
      AssemblySequence: ITEMS.filter((x) => x.mark === i.mark).map((x) => ({ Sequence: x.sequence })),
      BillOfMaterials: { BillOfMaterial: { PieceMark: i.mark, Shape: i.shape, Dimensions: i.dimensions, Grade: i.grade, Length: String(i.length) } },
    })),
  }),
  ProductionControlLoad_Get: () => ({
    Successful: '1',
    ResultData: {
      ProductionControlLoad: loads.map((l) => ({
        ProductionControlLoadID: String(l.id),
        ProductionControlID: String(JOB.pcid),
        LoadNumber: l.number,
        Capacity: String(l.capacity),
        ShippingDestinationGroupID: '',
        Shipped: l.shipped ? '1' : '0',
        ShippedDate: l.shipDate ?? '',
        ...(l.material.length ? { Material: l.material.length === 1 ? materialJson(l.material[0]) : l.material.map(materialJson) } : {}),
      })),
    },
  }),
  ProductionControlLoadRemaining_Get: () => ({
    Successful: '1',
    ResultData: {
      ProductionControlLoadRemaining: ITEMS.filter((i) => i.total - assigned(i) > 0).map((i) => ({
        ProductionControlItemID: String(i.itemId),
        ProductionControlID: String(JOB.pcid),
        MainMarkDisplay: i.mark,
        SequenceID: String(i.sequenceId),
        SequenceDisplay: i.sequence,
        ShippingWeightEach: String(i.kg),
        TotalQuantity: String(i.total),
        QuantityRemaining: String(i.total - assigned(i)),
        Shape: { '@ID': '1', '#text': i.shape },
        Dimensions: { '@Metric': '0', '#text': i.dimensions },
        Grade: { '@ID': '1', '#text': i.grade },
      })),
    },
  }),
  ProductionControlLoad_InputOptions_Get: () => ({
    ResultData: { InputOptions: { NextLoadNumber: `${Math.max(0, ...loads.map((l) => Number.parseInt(l.number, 10) || 0)) + 1}`, DefaultCapacity: '19958' } },
  }),
  ProductionControlLoad_IsDuplicateLoadNumber: (f) => ({ ResultData: { IsDuplicate: loads.some((l) => l.number.toUpperCase() === String(f.LoadNumber).toUpperCase()) ? '1' : '0' } }),
  ProductionControlLoad_Insert: (f) => {
    if (!f.Capacity) throw new Refused('Capacity is required.');
    if (loads.some((l) => l.number.toUpperCase() === String(f.LoadNumber).toUpperCase())) throw new Refused(`Load ${f.LoadNumber} already exists.`);
    const load = { id: nextId++, number: String(f.LoadNumber), shipped: false, capacity: f.Capacity, material: [] };
    loads.push(load);
    return { ResultData: { ProductionControlLoad: { ProductionControlLoadID: String(load.id) } } };
  },
  ProductionControlLoad_Delete: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    if (load.material.length) throw new Refused('A load with material on it can’t be deleted.');
    loads.splice(loads.indexOf(load), 1);
    return { Successful: '1' };
  },
  ProductionControlLoadMaterial_Add: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    openLoad(load);
    for (const a of list(f.AddItem)) {
      const item = ITEMS.find((i) => i.itemId === Number(a.ProductionControlItemID) && i.sequenceId === Number(a.SequenceID));
      if (!item) throw new Refused(`Item ${a.ProductionControlItemID} sequence ${a.SequenceID} isn't on the job.`);
      const qty = Number(a.Quantity) || 1;
      if (assigned(item) + qty > item.total) throw new Refused(`Only ${item.total - assigned(item)} of ${item.mark} left to assign.`);
      const existing = load.material.find((m) => m.item === item);
      if (existing) existing.qty += qty;
      else load.material.push({ truckId: nextId++, item, qty, loaded: [], returned: [] });
    }
    return { Successful: '1' };
  },
  ProductionControlLoadMaterial_Remove: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    openLoad(load);
    for (const r of list(f.RemoveItem)) {
      const m = materialById(load, r.ProductionControlItemTruckID);
      const qty = Number(r.Quantity) || m.qty;
      if (m.qty - qty < sum(m.loaded)) throw new Refused(`Unload ${m.item.mark} before removing it.`);
      m.qty -= qty;
      if (m.qty <= 0) load.material.splice(load.material.indexOf(m), 1);
    }
    return { Successful: '1' };
  },
  ProductionControlLoadMaterial_Load: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    openLoad(load);
    for (const l of list(f.LoadItem)) {
      const m = materialById(load, l.ProductionControlItemTruckID);
      const qty = Number(l.Quantity) || 1;
      if (sum(m.loaded) + qty > m.qty) throw new Refused(`Quantity to load for ${m.item.mark} is more than is assigned to the load.`);
      m.loaded.push({ id: nextId++, qty });
    }
    return { Successful: '1' };
  },
  ProductionControlLoadMaterial_Unload: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    openLoad(load);
    for (const u of list(f.UnloadItem)) {
      const m = load.material.find((x) => x.loaded.some((e) => e.id === Number(u.ProductionControlItemTrucksLoadID)));
      if (!m) throw new Refused(`Load entry ${u.ProductionControlItemTrucksLoadID} not found.`);
      const entry = m.loaded.find((e) => e.id === Number(u.ProductionControlItemTrucksLoadID));
      entry.qty -= Number(u.Quantity) || entry.qty;
      if (entry.qty <= 0) m.loaded.splice(m.loaded.indexOf(entry), 1);
    }
    return { Successful: '1' };
  },
  ProductionControlLoad_Ship: (f) => {
    const load = loadById(f.ProductionControlLoadID);
    if (!load.material.length || load.material.some((m) => sum(m.loaded) < m.qty)) throw new Refused('Every piece on the load has to be loaded before it ships.');
    Object.assign(load, { shipped: true, shipDate: f.ShippedDate ?? today() });
    return { Successful: '1' };
  },
  ProductionControlLoad_Unship: (f) => {
    Object.assign(loadById(f.ProductionControlLoadID), { shipped: false, shipDate: '' });
    return { Successful: '1' };
  },
  ProductionControlLoadMaterial_Return: () => { throw new Refused('Returns from the jobsite are not allowed.'); },
  ProductionControlLoadMaterial_Unreturn: () => { throw new Refused('Nothing to unreturn.'); },
};

function send(res, status, body) {
  res.writeHead(status, { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' });
  res.end(JSON.stringify(body));
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') return send(res, 200, { status: 'ok', mode: 'fake', writesEnabled: true });
  if (req.headers.authorization !== `Bearer ${key}`) return send(res, 401, { title: 'Unauthorized' });
  const m = /^\/api\/powerfab\/([A-Za-z0-9_]+)$/.exec(req.url ?? '');
  if (req.method !== 'POST' || !m) return send(res, 404, { title: 'Not found' });
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    const fn = commands[m[1]];
    if (!fn) return send(res, 422, { title: 'PowerFab refused the command', detail: `The fake PowerFab doesn't do ${m[1]}.` });
    try {
      const reply = fn(body ? JSON.parse(body) : {});
      // A little delay, like the real thing, so "sending" rows can be seen.
      setTimeout(() => send(res, 200, reply), Number(process.env.FAKE_DELAY_MS ?? 120));
    } catch (err) {
      if (err instanceof Refused) send(res, 422, { title: 'PowerFab refused the command', detail: err.message });
      else send(res, 500, { title: 'Fake PowerFab error', detail: err.message });
    }
  });
});

server.listen(port, '127.0.0.1', () => console.log(`Fake PowerFab bridge on http://127.0.0.1:${port} (key "${key}"), job ${JOB.number}`));
