import { createTLStore, defaultShapeUtils, defaultBindingUtils } from 'tldraw'

const store = createTLStore()
const schema = store.schema.serialize()
console.log('serialized schema top-level keys:', Object.keys(schema).join(','))
console.log('schema.records keys:', Object.keys(schema.records || {}).join(','))
console.log('schema.records.shape keys:', Object.keys((schema.records && schema.records.shape) || {}).join(','))

const store2 = createTLStore({ shapeUtils: defaultShapeUtils, bindingUtils: defaultBindingUtils })
const snap = store2.getStoreSnapshot()
console.log('snapshot keys:', Object.keys(snap).join(','))
console.log('snapshot store record count:', Object.keys(snap.store).length)
console.log('snapshot store ids:', Object.keys(snap.store).join(' | '))
console.log('defaultShapeUtils len:', defaultShapeUtils.map((s) => s.type).join(','))
console.log('defaultBindingUtils len:', defaultBindingUtils.map((s) => s.type).join(','))
