import assert from 'node:assert/strict'
import test from 'node:test'
import {
  defaultParameters,
  fromDocument,
  type NodeType,
  type WorkflowDocument,
} from '../src/types/workflow.ts'

function nodeDocument(type: NodeType, parameters: Record<string, unknown>): WorkflowDocument {
  return {
    schemaVersion: '1.6',
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

test('节点加载时合并默认参数且已有值优先', () => {
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
