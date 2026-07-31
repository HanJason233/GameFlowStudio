import assert from 'node:assert/strict'
import test from 'node:test'
import {
  defaultParameters,
  fromDocument,
  toDocument,
  type NodeType,
  type WorkflowDocument,
} from '../src/types/workflow.ts'

function nodeDocument(type: NodeType, parameters: Record<string, unknown>): WorkflowDocument {
  return {
    schemaVersion: '1.5',
    project: {
      id: 'test',
      name: 'test',
      createdAt: '2026-01-01T00:00:00.000Z',
      updatedAt: '2026-01-01T00:00:00.000Z',
    },
    variables: {},
    nodes: [{
      id: 'node',
      type,
      position: { x: 10, y: 20 },
      data: { displayName: type, parameters },
    }],
    edges: [],
  }
}

test('节点默认参数不再包含前后台模式', () => {
  const legacyModeNames = ['clickMode', 'inputMode', 'captureMode', 'windowActionMode']

  for (const [type, parameters] of Object.entries(defaultParameters)) {
    for (const modeName of legacyModeNames)
      assert.equal(modeName in parameters, false, `${type}.${modeName}`)
  }
})

test('导入旧节点时移除节点级前后台参数', async t => {
  const cases: Array<[NodeType, string]> = [
    ['mouseClick', 'clickMode'],
    ['mouseDrag', 'inputMode'],
    ['imageCondition', 'captureMode'],
    ['minimizeWindow', 'windowActionMode'],
  ]

  for (const [type, modeName] of cases) {
    await t.test(type, () => {
      const source = nodeDocument(type, {
        [modeName]: 'background',
        customParameter: '保留',
      })
      const graph = fromDocument(source)
      const saved = toDocument(source, graph.nodes, graph.edges)

      assert.equal(modeName in graph.nodes[0].data.parameters, false)
      assert.equal(modeName in saved.nodes[0].data.parameters, false)
      assert.equal(saved.nodes[0].data.parameters.customParameter, '保留')
    })
  }
})

test('旧图加载时为所有节点合并默认参数且已有值优先', () => {
  for (const type of Object.keys(defaultParameters) as NodeType[]) {
    const defaults = defaultParameters[type]
    const overriddenName = Object.keys(defaults)[0]
    const existing = overriddenName
      ? { [overriddenName]: '已有值', customParameter: type }
      : { customParameter: type }
    const graph = fromDocument(nodeDocument(type, existing))
    const parameters = graph.nodes[0].data.parameters

    for (const [name, value] of Object.entries(defaults)) {
      assert.deepEqual(parameters[name], name === overriddenName ? '已有值' : value, `${type}.${name}`)
    }
    assert.equal(parameters.customParameter, type)
  }
})
