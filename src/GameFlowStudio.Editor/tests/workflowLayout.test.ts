import assert from 'node:assert/strict'
import test from 'node:test'
import type { Edge } from '@xyflow/react'
import {
  arrangeWorkflowNodes,
  selectWorkflowFocusNodeIds,
} from '../src/workflowLayout.ts'
import type { GameFlowNode, NodeType } from '../src/types/workflow.ts'

test('线性流程会紧凑换行且不修改输入', () => {
  const nodes = Array.from({ length: 8 }, (_, index) => createNode(String(index + 1)))
  const edges = nodes.slice(1).map((node, index) =>
    createEdge(`edge-${index}`, nodes[index].id, node.id))
  const original = structuredClone(nodes)

  const arranged = arrangeWorkflowNodes(nodes, edges, { maxColumns: 4 })

  assert.deepEqual(nodes, original)
  assert.deepEqual(
    arranged.slice(0, 4).map(node => node.position.x),
    [48, 238, 428, 618],
  )
  assert.deepEqual(
    arranged.slice(4).map(node => node.position.x),
    [48, 238, 428, 618],
  )
  assert.equal(new Set(arranged.slice(0, 4).map(node => node.position.y)).size, 1)
  assert.equal(new Set(arranged.slice(4).map(node => node.position.y)).size, 1)
  assert.ok(arranged[4].position.y > arranged[0].position.y)
})

test('长流程会按窄屏、常规和宽屏画布限制每行列数', () => {
  const nodes = Array.from({ length: 20 }, (_, index) => createNode(String(index + 1)))
  const edges = nodes.slice(1).map((node, index) =>
    createEdge(`edge-${index}`, nodes[index].id, node.id))

  const narrow = arrangeWorkflowNodes(nodes, edges, { viewportWidth: 400 })
  const regular = arrangeWorkflowNodes(nodes, edges, { viewportWidth: 850 })
  const wide = arrangeWorkflowNodes(nodes, edges, { viewportWidth: 1600 })
  const fallback = arrangeWorkflowNodes(nodes, edges, { viewportWidth: Number.NaN })

  assert.equal(Math.max(...narrow.map(node => node.position.x)), 428)
  assert.ok(narrow[3].position.y > narrow[0].position.y)
  assert.equal(Math.max(...regular.map(node => node.position.x)), 998)
  assert.ok(regular[6].position.y > regular[0].position.y)
  assert.equal(Math.max(...wide.map(node => node.position.x)), 1378)
  assert.ok(wide[8].position.y > wide[0].position.y)
  assert.equal(Math.max(...fallback.map(node => node.position.x)), 998)
})

test('条件分支按父节点重心排列并保持 true 在 false 上方', () => {
  const nodes = [
    createNode('condition', 'colorCondition'),
    createNode('false-node'),
    createNode('true-node'),
    createNode('merge'),
  ]
  const edges = [
    createEdge('false-edge', 'condition', 'false-node', 'false'),
    createEdge('true-edge', 'condition', 'true-node', 'true'),
    createEdge('false-merge', 'false-node', 'merge'),
    createEdge('true-merge', 'true-node', 'merge'),
  ]

  const arranged = arrangeWorkflowNodes(nodes, edges)
  const byId = new Map(arranged.map(node => [node.id, node]))
  const trueNode = byId.get('true-node')!
  const falseNode = byId.get('false-node')!

  assert.equal(trueNode.position.x, falseNode.position.x)
  assert.ok(trueNode.position.y < falseNode.position.y)
  assert.equal(byId.get('condition')!.position.x, 48)
  assert.equal(byId.get('merge')!.position.x, 428)
})

test('额外入边不会破坏同一条件节点的 true 和 false 顺序', () => {
  const nodes = [
    createNode('start', 'start'),
    createNode('a', 'colorCondition'),
    createNode('c', 'imageCondition'),
    createNode('z'),
    createNode('false-node'),
    createNode('true-node'),
    createNode('end', 'end'),
  ]
  const edges = [
    createEdge('start-a', 'start', 'a'),
    createEdge('a-c', 'a', 'c', 'true'),
    createEdge('a-z', 'a', 'z', 'false'),
    createEdge('c-true', 'c', 'true-node', 'true'),
    createEdge('c-false', 'c', 'false-node', 'false'),
    createEdge('z-true', 'z', 'true-node'),
    createEdge('true-end', 'true-node', 'end'),
    createEdge('false-end', 'false-node', 'end'),
  ]

  const arranged = arrangeWorkflowNodes(nodes, edges)
  const byId = new Map(arranged.map(node => [node.id, node]))

  assert.equal(byId.get('true-node')!.position.x, byId.get('false-node')!.position.x)
  assert.ok(byId.get('true-node')!.position.y < byId.get('false-node')!.position.y)
})

test('实测高度用于避免同列卡片重叠', () => {
  const nodes = [
    createNode('condition', 'imageCondition'),
    createNode('true-node', 'mouseClick', 80),
    createNode('false-node', 'mouseClick', 120),
  ]
  const edges = [
    createEdge('true-edge', 'condition', 'true-node', 'true'),
    createEdge('false-edge', 'condition', 'false-node', 'false'),
  ]

  const arranged = arrangeWorkflowNodes(nodes, edges)
  const byId = new Map(arranged.map(node => [node.id, node]))
  const trueNode = byId.get('true-node')!
  const falseNode = byId.get('false-node')!

  assert.ok(falseNode.position.y - trueNode.position.y >= 112)
})

test('断开的流程组件按实际高度分隔且忽略悬空边', () => {
  const nodes = [
    createNode('a', 'start', 120),
    createNode('b', 'end', 120),
    createNode('c', 'start', 100),
    createNode('d', 'end', 100),
  ]
  const edges = [
    createEdge('a-b', 'a', 'b'),
    createEdge('c-d', 'c', 'd'),
    createEdge('missing-c', 'missing', 'c'),
  ]

  const arranged = arrangeWorkflowNodes(nodes, edges)
  const byId = new Map(arranged.map(node => [node.id, node]))
  const firstBottom = Math.max(
    byId.get('a')!.position.y + 120,
    byId.get('b')!.position.y + 120,
  )
  const secondTop = Math.min(
    byId.get('c')!.position.y,
    byId.get('d')!.position.y,
  )

  assert.ok(secondTop - firstBottom >= 88)
})

test('环路、孤立节点和重复调用都有有限且确定的位置', () => {
  const nodes = [
    createNode('a'),
    createNode('b'),
    createNode('c'),
    createNode('isolated'),
  ]
  const edges = [
    createEdge('a-b', 'a', 'b'),
    createEdge('b-c', 'b', 'c'),
    createEdge('c-a', 'c', 'a'),
  ]

  const first = arrangeWorkflowNodes(nodes, edges, { maxColumns: 3 })
  const second = arrangeWorkflowNodes(
    nodes.map(node => ({ ...node, position: { x: 999, y: -999 } })),
    edges,
    { maxColumns: 3 },
  )

  assert.deepEqual(
    first.map(node => ({ id: node.id, position: node.position })),
    second.map(node => ({ id: node.id, position: node.position })),
  )
  assert.ok(first.every(node =>
    Number.isFinite(node.position.x) && Number.isFinite(node.position.y)))
  assert.equal(
    new Set(first.map(node => `${node.position.x},${node.position.y}`)).size,
    nodes.length,
  )
})

test('相同节点编号的不同脚本不会互相污染布局状态', () => {
  const nodes = [createNode('1', 'start'), createNode('2'), createNode('3', 'end')]
  const linearEdges = [
    createEdge('1-2', '1', '2'),
    createEdge('2-3', '2', '3'),
  ]
  const branchEdges = [
    createEdge('1-2', '1', '2', 'true'),
    createEdge('1-3', '1', '3', 'false'),
  ]

  const firstLinear = arrangeWorkflowNodes(nodes, linearEdges)
  const branch = arrangeWorkflowNodes(nodes, branchEdges)
  const secondLinear = arrangeWorkflowNodes(nodes, linearEdges)

  assert.deepEqual(firstLinear, secondLinear)
  assert.notDeepEqual(
    firstLinear.map(node => node.position),
    branch.map(node => node.position),
  )
})

test('八千节点录制规模仍会完成紧凑且有限的布局', () => {
  const nodes = Array.from({ length: 8000 }, (_, index) => createNode(String(index + 1)))
  const edges = nodes.slice(1).map((node, index) =>
    createEdge(`edge-${index}`, nodes[index].id, node.id))

  const arranged = arrangeWorkflowNodes(nodes, edges, { maxColumns: 6 })

  assert.equal(arranged.length, nodes.length)
  assert.equal(Math.max(...arranged.map(node => node.position.x)), 998)
  assert.ok(arranged.every(node =>
    Number.isFinite(node.position.x) && Number.isFinite(node.position.y)))
})

test('大图聚焦从开始节点沿连接顺序选取连续节点', () => {
  const nodes = [
    createNode('unrelated'),
    createNode('b'),
    createNode('start', 'start'),
    createNode('a', 'colorCondition'),
    createNode('false-node'),
    createNode('true-node'),
  ]
  const edges = [
    createEdge('start-a', 'start', 'a'),
    createEdge('a-false', 'a', 'false-node', 'false'),
    createEdge('a-true', 'a', 'true-node', 'true'),
    createEdge('true-b', 'true-node', 'b'),
  ]

  assert.deepEqual(
    selectWorkflowFocusNodeIds(nodes, edges, 5),
    ['start', 'a', 'true-node', 'false-node', 'b'],
  )
})

function createNode(
  id: string,
  nodeType: NodeType = 'mouseClick',
  height?: number,
): GameFlowNode {
  return {
    id,
    type: 'gameNode',
    position: { x: -500, y: 800 },
    measured: height === undefined ? undefined : { width: 112, height },
    data: {
      nodeType,
      displayName: id,
      parameters: {},
    },
  }
}

function createEdge(
  id: string,
  source: string,
  target: string,
  sourceHandle = 'output',
): Edge {
  return {
    id,
    source,
    sourceHandle,
    target,
    targetHandle: 'input',
  }
}
