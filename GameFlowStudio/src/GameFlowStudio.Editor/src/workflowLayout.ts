import type { Edge } from '@xyflow/react'
import type { GameFlowNode } from './types/workflow'

const horizontalGap = 300
const verticalGap = 170
const firstColumnX = 100
const firstRowY = 100

/** Places nodes from left to right following the workflow edges. */
export function arrangeWorkflowNodes(nodes: GameFlowNode[], edges: Edge[]): GameFlowNode[] {
  if (nodes.length < 2) return nodes

  const order = new Map(nodes.map((node, index) => [node.id, index]))
  const incoming = new Map<string, Edge[]>()
  const outgoing = new Map<string, string[]>()
  const indegree = new Map(nodes.map(node => [node.id, 0]))
  for (const node of nodes) {
    incoming.set(node.id, [])
    outgoing.set(node.id, [])
  }
  for (const edge of edges) {
    if (!indegree.has(edge.source) || !indegree.has(edge.target)) continue
    incoming.get(edge.target)?.push(edge)
    outgoing.get(edge.source)?.push(edge.target)
    indegree.set(edge.target, (indegree.get(edge.target) ?? 0) + 1)
  }

  const levels = new Map<string, number>()
  const pendingIncoming = new Map(indegree)
  const roots = nodes.filter(node => pendingIncoming.get(node.id) === 0).sort(byOriginalOrder(order))
  for (const root of roots) levels.set(root.id, 0)
  const queue = roots.map(node => node.id)
  let queueIndex = 0
  while (queueIndex < queue.length) {
    const source = queue[queueIndex++]
    const sourceLevel = levels.get(source) ?? 0
    for (const target of outgoing.get(source) ?? []) {
      levels.set(target, Math.max(levels.get(target) ?? 0, sourceLevel + 1))
      const remaining = (pendingIncoming.get(target) ?? 1) - 1
      pendingIncoming.set(target, remaining)
      if (remaining === 0) queue.push(target)
    }
  }

  // Cycles are not expected in a normal script, but still place them instead of
  // leaving nodes at arbitrary positions when a user is midway through editing.
  let maxLevel = Math.max(-1, ...levels.values())
  const unresolved = nodes.filter(node => !levels.has(node.id)).sort(byOriginalOrder(order))
  for (const seed of unresolved) {
    if (levels.has(seed.id)) continue
    levels.set(seed.id, ++maxLevel)
    const walk = [seed.id]
    for (let index = 0; index < walk.length; index++) {
      const source = walk[index]
      const sourceLevel = levels.get(source) ?? maxLevel
      for (const target of outgoing.get(source) ?? []) {
        if (levels.has(target)) continue
        levels.set(target, sourceLevel + 1)
        maxLevel = Math.max(maxLevel, sourceLevel + 1)
        walk.push(target)
      }
    }
  }

  const columns = new Map<number, GameFlowNode[]>()
  for (const node of nodes) {
    const level = levels.get(node.id) ?? 0
    const column = columns.get(level) ?? []
    column.push(node)
    columns.set(level, column)
  }
  const maxColumnSize = Math.max(...Array.from(columns.values(), column => column.length))

  return nodes.map(node => {
    const level = levels.get(node.id) ?? 0
    const column = columns.get(level) ?? [node]
    const sortedColumn = [...column].sort((left, right) => compareNodes(left, right, incoming, order))
    const row = sortedColumn.findIndex(item => item.id === node.id)
    const y = firstRowY + (row + (maxColumnSize - column.length) / 2) * verticalGap
    return { ...node, position: { x: firstColumnX + level * horizontalGap, y } }
  })
}

function compareNodes(
  left: GameFlowNode,
  right: GameFlowNode,
  incoming: Map<string, Edge[]>,
  order: Map<string, number>,
): number {
  const leftRank = Math.min(...(incoming.get(left.id) ?? []).map(edge => edge.sourceHandle === 'true' ? 0 : edge.sourceHandle === 'false' ? 1 : 2), 2)
  const rightRank = Math.min(...(incoming.get(right.id) ?? []).map(edge => edge.sourceHandle === 'true' ? 0 : edge.sourceHandle === 'false' ? 1 : 2), 2)
  return leftRank - rightRank || (order.get(left.id) ?? 0) - (order.get(right.id) ?? 0)
}

function byOriginalOrder(order: Map<string, number>) {
  return (left: GameFlowNode, right: GameFlowNode) => (order.get(left.id) ?? 0) - (order.get(right.id) ?? 0)
}
