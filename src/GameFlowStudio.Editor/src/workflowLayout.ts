import type { Edge } from '@xyflow/react'
import type { GameFlowNode } from './types/workflow'

const firstColumnX = 48
const firstRowY = 48
const nodeWidth = 112
const defaultNodeHeight = 72
const horizontalStep = 190
const verticalGap = 32
const bandGap = 48
const componentGap = 88
const defaultColumnsPerBand = 6
const minimumColumnsPerBand = 3
const maximumColumnsPerBand = 8
const targetOverviewZoom = .72

export interface WorkflowLayoutOptions {
  viewportWidth?: number
  maxColumns?: number
}

interface ComponentLayout {
  positions: Map<string, { x: number; y: number }>
  height: number
}

/** 按连接关系分层，并将长流程折成紧凑的多行布局。 */
export function arrangeWorkflowNodes(
  nodes: GameFlowNode[],
  edges: Edge[],
  options: WorkflowLayoutOptions = {},
): GameFlowNode[] {
  if (nodes.length < 2) return nodes

  const order = new Map(nodes.map((node, index) => [node.id, index]))
  const nodeIds = new Set(nodes.map(node => node.id))
  const validEdges = edges.filter(edge => nodeIds.has(edge.source) && nodeIds.has(edge.target))
  const components = findWeakComponents(nodes, validEdges, order)
  const componentByNode = new Map<string, number>()
  components.forEach((component, componentIndex) =>
    component.forEach(node => componentByNode.set(node.id, componentIndex)))
  const edgesByComponent = components.map(() => [] as Edge[])
  for (const edge of validEdges) {
    const componentIndex = componentByNode.get(edge.source)
    if (componentIndex !== undefined && componentIndex === componentByNode.get(edge.target))
      edgesByComponent[componentIndex].push(edge)
  }
  const positions = new Map<string, { x: number; y: number }>()
  let componentTop = firstRowY

  for (let componentIndex = 0; componentIndex < components.length; componentIndex++) {
    const layout = layoutComponent(
      components[componentIndex],
      edgesByComponent[componentIndex],
      order,
      options,
      componentTop,
    )
    for (const [nodeId, position] of layout.positions) positions.set(nodeId, position)
    componentTop += layout.height + componentGap
  }

  return nodes.map(node => ({
    ...node,
    position: positions.get(node.id) ?? { ...node.position },
  }))
}

/** 从开始节点沿流程边广度遍历，返回适合大图首屏聚焦的连续节点。 */
export function selectWorkflowFocusNodeIds(
  nodes: GameFlowNode[],
  edges: Edge[],
  limit = 12,
): string[] {
  if (nodes.length === 0 || limit <= 0) return []

  const order = new Map(nodes.map((node, index) => [node.id, index]))
  const nodeIds = new Set(nodes.map(node => node.id))
  const outgoing = new Map(nodes.map(node => [node.id, [] as Edge[]]))
  for (const edge of edges) {
    if (!nodeIds.has(edge.source) || !nodeIds.has(edge.target)) continue
    outgoing.get(edge.source)?.push(edge)
  }
  for (const adjacent of outgoing.values()) {
    adjacent.sort((left, right) =>
      branchRank(left.sourceHandle) - branchRank(right.sourceHandle)
      || (order.get(left.target) ?? 0) - (order.get(right.target) ?? 0))
  }

  const first = nodes.find(node => node.data.nodeType === 'start')
    ?? [...nodes].sort((left, right) =>
      left.position.x - right.position.x
      || left.position.y - right.position.y
      || (order.get(left.id) ?? 0) - (order.get(right.id) ?? 0))[0]
  if (!first) return []
  const selected: string[] = []
  const visited = new Set<string>([first.id])
  const queue = [first.id]
  for (let index = 0; index < queue.length && selected.length < limit; index++) {
    const nodeId = queue[index]
    selected.push(nodeId)
    for (const edge of outgoing.get(nodeId) ?? []) {
      if (visited.has(edge.target)) continue
      visited.add(edge.target)
      queue.push(edge.target)
    }
  }
  return selected
}

function layoutComponent(
  nodes: GameFlowNode[],
  edges: Edge[],
  order: Map<string, number>,
  options: WorkflowLayoutOptions,
  top: number,
): ComponentLayout {
  const incoming = new Map<string, Edge[]>()
  const outgoing = new Map<string, Edge[]>()
  const indegree = new Map(nodes.map(node => [node.id, 0]))
  for (const node of nodes) {
    incoming.set(node.id, [])
    outgoing.set(node.id, [])
  }
  for (const edge of edges) {
    incoming.get(edge.target)?.push(edge)
    outgoing.get(edge.source)?.push(edge)
    indegree.set(edge.target, (indegree.get(edge.target) ?? 0) + 1)
  }

  const levels = calculateLevels(nodes, outgoing, indegree, order)
  const levelCount = Math.max(...levels.values()) + 1
  const columns = Array.from({ length: levelCount }, () => [] as GameFlowNode[])
  for (const node of nodes) columns[levels.get(node.id) ?? 0].push(node)
  orderColumns(columns, incoming, outgoing, order)

  const columnsPerBand = resolveColumnsPerBand(levelCount, options)
  const positions = new Map<string, { x: number; y: number }>()
  let bandTop = top

  for (let start = 0; start < columns.length; start += columnsPerBand) {
    const band = columns.slice(start, start + columnsPerBand)
    const columnHeights = band.map(column => columnHeight(column))
    const bandHeight = Math.max(defaultNodeHeight, ...columnHeights)

    band.forEach((column, columnIndex) => {
      let y = bandTop + (bandHeight - columnHeights[columnIndex]) / 2
      for (const node of column) {
        positions.set(node.id, {
          x: firstColumnX + columnIndex * horizontalStep,
          y,
        })
        y += measuredHeight(node) + verticalGap
      }
    })

    bandTop += bandHeight
    if (start + columnsPerBand < columns.length) bandTop += bandGap
  }

  return { positions, height: Math.max(defaultNodeHeight, bandTop - top) }
}

function calculateLevels(
  nodes: GameFlowNode[],
  outgoing: Map<string, Edge[]>,
  indegree: Map<string, number>,
  order: Map<string, number>,
): Map<string, number> {
  const levels = new Map<string, number>()
  const pendingIncoming = new Map(indegree)
  const processed = new Set<string>()
  const roots = nodes
    .filter(node => pendingIncoming.get(node.id) === 0)
    .sort(byOriginalOrder(order))
  for (const root of roots) levels.set(root.id, 0)

  const queue = roots.map(node => node.id)
  for (let queueIndex = 0; queueIndex < queue.length; queueIndex++) {
    const source = queue[queueIndex]
    processed.add(source)
    const sourceLevel = levels.get(source) ?? 0
    for (const edge of outgoing.get(source) ?? []) {
      levels.set(edge.target, Math.max(levels.get(edge.target) ?? 0, sourceLevel + 1))
      const remaining = (pendingIncoming.get(edge.target) ?? 1) - 1
      pendingIncoming.set(edge.target, remaining)
      if (remaining === 0) queue.push(edge.target)
    }
  }

  // 正常脚本不允许环路；编辑过程中若暂时形成环路，也要给出稳定且有限的布局。
  let maxLevel = Math.max(
    -1,
    ...nodes
      .filter(node => processed.has(node.id))
      .map(node => levels.get(node.id) ?? 0),
  )
  const placedCycleNodes = new Set<string>()
  const unresolved = nodes
    .filter(node => !processed.has(node.id))
    .sort(byOriginalOrder(order))
  for (const seed of unresolved) {
    if (placedCycleNodes.has(seed.id)) continue
    levels.set(seed.id, ++maxLevel)
    placedCycleNodes.add(seed.id)
    const walk = [seed.id]
    for (let index = 0; index < walk.length; index++) {
      const source = walk[index]
      const sourceLevel = levels.get(source) ?? maxLevel
      for (const edge of outgoing.get(source) ?? []) {
        if (processed.has(edge.target) || placedCycleNodes.has(edge.target)) continue
        levels.set(edge.target, sourceLevel + 1)
        maxLevel = Math.max(maxLevel, sourceLevel + 1)
        placedCycleNodes.add(edge.target)
        walk.push(edge.target)
      }
    }
  }

  return levels
}

function orderColumns(
  columns: GameFlowNode[][],
  incoming: Map<string, Edge[]>,
  outgoing: Map<string, Edge[]>,
  order: Map<string, number>,
): void {
  for (const column of columns) column.sort(byOriginalOrder(order))
  const rowIndex = new Map<string, number>()
  const refreshRows = () => {
    rowIndex.clear()
    for (const column of columns)
      column.forEach((node, index) => rowIndex.set(node.id, index))
  }

  for (let pass = 0; pass < 2; pass++) {
    refreshRows()
    for (let level = 1; level < columns.length; level++) {
      sortByBarycenter(
        columns[level],
        node => predecessorScore(node.id, incoming, rowIndex),
        order,
        (left, right) => conditionSiblingOrder(left.id, right.id, incoming),
      )
      columns[level].forEach((node, index) => rowIndex.set(node.id, index))
    }

    refreshRows()
    for (let level = columns.length - 2; level >= 0; level--) {
      sortByBarycenter(
        columns[level],
        node => successorScore(node.id, outgoing, rowIndex),
        order,
      )
      columns[level].forEach((node, index) => rowIndex.set(node.id, index))
    }
  }

  // 最后一遍从左到右，确保同一条件节点的 true 分支稳定地位于 false 分支上方。
  refreshRows()
  for (let level = 1; level < columns.length; level++) {
    sortByBarycenter(
      columns[level],
      node => predecessorScore(node.id, incoming, rowIndex),
      order,
      (left, right) => conditionSiblingOrder(left.id, right.id, incoming),
    )
    columns[level].forEach((node, index) => rowIndex.set(node.id, index))
  }
}

function predecessorScore(
  nodeId: string,
  incoming: Map<string, Edge[]>,
  rowIndex: Map<string, number>,
): number | undefined {
  const candidates = (incoming.get(nodeId) ?? [])
    .filter(edge => rowIndex.has(edge.source))
  const conditionalCandidates = candidates
    .filter(edge => edge.sourceHandle === 'true' || edge.sourceHandle === 'false')
  const scores = (conditionalCandidates.length > 0 ? conditionalCandidates : candidates)
    .map(edge => (rowIndex.get(edge.source) ?? 0) + branchOffset(edge.sourceHandle))
  return scores.length === 0
    ? undefined
    : scores.reduce((sum, value) => sum + value, 0) / scores.length
}

function successorScore(
  nodeId: string,
  outgoing: Map<string, Edge[]>,
  rowIndex: Map<string, number>,
): number | undefined {
  const scores = (outgoing.get(nodeId) ?? [])
    .filter(edge => rowIndex.has(edge.target))
    .map(edge => rowIndex.get(edge.target) ?? 0)
  return scores.length === 0
    ? undefined
    : scores.reduce((sum, value) => sum + value, 0) / scores.length
}

function sortByBarycenter(
  column: GameFlowNode[],
  score: (node: GameFlowNode) => number | undefined,
  order: Map<string, number>,
  hardOrder?: (left: GameFlowNode, right: GameFlowNode) => number,
): void {
  column.sort((left, right) => {
    const constrained = hardOrder?.(left, right) ?? 0
    if (constrained !== 0) return constrained
    const leftScore = score(left)
    const rightScore = score(right)
    if (leftScore !== undefined && rightScore !== undefined && leftScore !== rightScore)
      return leftScore - rightScore
    if (leftScore !== undefined && rightScore === undefined) return -1
    if (leftScore === undefined && rightScore !== undefined) return 1
    return (order.get(left.id) ?? 0) - (order.get(right.id) ?? 0)
  })
}

function conditionSiblingOrder(
  leftId: string,
  rightId: string,
  incoming: Map<string, Edge[]>,
): number {
  const leftEdges = incoming.get(leftId) ?? []
  const rightEdges = incoming.get(rightId) ?? []
  for (const leftEdge of leftEdges) {
    if (leftEdge.sourceHandle !== 'true' && leftEdge.sourceHandle !== 'false') continue
    const opposite = leftEdge.sourceHandle === 'true' ? 'false' : 'true'
    if (rightEdges.some(edge =>
      edge.source === leftEdge.source && edge.sourceHandle === opposite))
      return leftEdge.sourceHandle === 'true' ? -1 : 1
  }
  return 0
}

function findWeakComponents(
  nodes: GameFlowNode[],
  edges: Edge[],
  order: Map<string, number>,
): GameFlowNode[][] {
  const nodeById = new Map(nodes.map(node => [node.id, node]))
  const neighbors = new Map(nodes.map(node => [node.id, new Set<string>()]))
  for (const edge of edges) {
    neighbors.get(edge.source)?.add(edge.target)
    neighbors.get(edge.target)?.add(edge.source)
  }

  const visited = new Set<string>()
  const components: GameFlowNode[][] = []
  const isolated: GameFlowNode[] = []
  for (const node of [...nodes].sort(byOriginalOrder(order))) {
    if (visited.has(node.id)) continue
    if ((neighbors.get(node.id)?.size ?? 0) === 0) {
      visited.add(node.id)
      isolated.push(node)
      continue
    }

    const component: GameFlowNode[] = []
    const queue = [node.id]
    visited.add(node.id)
    for (let index = 0; index < queue.length; index++) {
      const nodeId = queue[index]
      const current = nodeById.get(nodeId)
      if (current) component.push(current)
      const adjacent = [...(neighbors.get(nodeId) ?? [])]
        .sort((left, right) => (order.get(left) ?? 0) - (order.get(right) ?? 0))
      for (const adjacentId of adjacent) {
        if (visited.has(adjacentId)) continue
        visited.add(adjacentId)
        queue.push(adjacentId)
      }
    }
    component.sort(byOriginalOrder(order))
    components.push(component)
  }
  if (isolated.length > 0) components.push(isolated)
  return components.sort((left, right) =>
    (order.get(left[0]?.id ?? '') ?? 0) - (order.get(right[0]?.id ?? '') ?? 0))
}

function resolveColumnsPerBand(
  levelCount: number,
  options: WorkflowLayoutOptions,
): number {
  const maxColumns = options.maxColumns
  if (typeof maxColumns === 'number' && Number.isFinite(maxColumns) && maxColumns > 0)
    return Math.min(levelCount, Math.max(1, Math.floor(maxColumns)))

  const viewportWidth = options.viewportWidth
  if (typeof viewportWidth !== 'number' || !Number.isFinite(viewportWidth) || viewportWidth <= 0)
    return Math.min(levelCount, defaultColumnsPerBand)

  const overviewWidth = viewportWidth / targetOverviewZoom
  const calculated = Math.floor(
    (overviewWidth - firstColumnX * 2 - nodeWidth) / horizontalStep,
  ) + 1
  return Math.min(
    levelCount,
    Math.max(
      minimumColumnsPerBand,
      Math.min(maximumColumnsPerBand, calculated),
    ),
  )
}

function columnHeight(column: GameFlowNode[]): number {
  if (column.length === 0) return 0
  return column.reduce((sum, node) => sum + measuredHeight(node), 0)
    + (column.length - 1) * verticalGap
}

function measuredHeight(node: GameFlowNode): number {
  const height = node.measured?.height ?? node.height
  return typeof height === 'number' && Number.isFinite(height) && height > 0
    ? height
    : defaultNodeHeight
}

function branchOffset(sourceHandle: string | null | undefined): number {
  if (sourceHandle === 'true') return -.28
  if (sourceHandle === 'false') return .28
  return 0
}

function branchRank(sourceHandle: string | null | undefined): number {
  if (sourceHandle === 'true') return 0
  if (sourceHandle === 'false') return 1
  return 2
}

function byOriginalOrder(order: Map<string, number>) {
  return (left: GameFlowNode, right: GameFlowNode) =>
    (order.get(left.id) ?? 0) - (order.get(right.id) ?? 0)
}
