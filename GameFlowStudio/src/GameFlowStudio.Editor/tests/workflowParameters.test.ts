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
    schemaVersion: '1.4',
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

function clickDocument(parameters: Record<string, unknown>): WorkflowDocument {
  return nodeDocument('mouseClick', parameters)
}

test('旧点击节点缺少 clickMode 时补为前台点击', () => {
  const graph = fromDocument(clickDocument({ x: 10, y: 20 }))

  assert.equal(graph.nodes[0].data.parameters.clickMode, 'foreground')
})

test('后台点击模式在编辑器导入导出后保持不变', () => {
  const source = clickDocument({
    clickMode: 'background',
    coordinateMode: 'relative',
    x: 10,
    y: 20,
  })
  const graph = fromDocument(source)

  const saved = toDocument(source, graph.nodes, graph.edges)

  assert.equal(saved.nodes[0].data.parameters.clickMode, 'background')
  assert.equal(saved.nodes[0].data.parameters.coordinateMode, 'relative')
})

test('旧输入、读取和窗口动作节点缺少模式时统一补为前台', async t => {
  const cases: Array<[NodeType, string]> = [
    ['mouseMove', 'inputMode'],
    ['mouseDrag', 'inputMode'],
    ['mouseScroll', 'inputMode'],
    ['keyPress', 'inputMode'],
    ['textInput', 'inputMode'],
    ['colorCondition', 'captureMode'],
    ['imageCondition', 'captureMode'],
    ['ocrCondition', 'captureMode'],
    ['closeProgram', 'windowActionMode'],
    ['minimizeWindow', 'windowActionMode'],
    ['setWindowResolution', 'windowActionMode'],
  ]

  for (const [type, modeName] of cases) {
    await t.test(type, () => {
      const graph = fromDocument(nodeDocument(type, {}))

      assert.equal(graph.nodes[0].data.parameters[modeName], 'foreground')
    })
  }
})

test('后台模式和已有参数在编辑器导入导出后保持不变', async t => {
  const cases: Array<[NodeType, string]> = [
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

      assert.equal(saved.nodes[0].data.parameters[modeName], 'background')
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
