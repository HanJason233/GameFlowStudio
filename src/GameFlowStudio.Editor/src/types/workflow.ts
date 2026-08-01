import type { Edge, Node } from '@xyflow/react'

export const nodeTypes = [
  'start', 'mouseClick', 'mouseMove', 'mouseDrag', 'mouseScroll', 'keyPress', 'textInput', 'wait',
  'colorCondition', 'imageCondition', 'ocrCondition', 'jump', 'runProgram', 'closeProgram',
  'minimizeWindow', 'arrangeWindows', 'setWindowResolution', 'end',
] as const

export type NodeType = (typeof nodeTypes)[number]

export type ExecutionStatus = 'none' | 'running' | 'success' | 'error'

export interface PersistedNodeData {
  displayName: string
  parameters: Record<string, unknown>
}

export type NodeDataUpdate = Partial<PersistedNodeData>

export interface EditorNodeData extends PersistedNodeData, Record<string, unknown> {
  nodeType: NodeType
  executionStatus?: ExecutionStatus
  group?: CanvasGroup
}

export type GameFlowNode = Node<EditorNodeData, 'gameNode' | 'groupNode'>

export interface CanvasGroup {
  id: string
  name: string
  nodeIds: string[]
  position: { x: number; y: number }
  width: number
  height: number
  collapsed: boolean
  externalEdges?: PersistedEdge[]
}

export interface PersistedEdge {
  id: string
  source: string
  sourceHandle: string
  target: string
  targetHandle: string
}

export function nextActionId(nodes: GameFlowNode[]): string {
  const numericIds = nodes.map(node => /^\d+$/.test(node.id) ? Number(node.id) : 0)
  return String(Math.max(0, ...numericIds) + 1)
}

export interface ProjectMetadata {
  id: string
  name: string
  createdAt: string
  updatedAt: string
}

export interface WorkflowDocument {
  schemaVersion: '1.8'
  project: ProjectMetadata
  variables: Record<string, { type: 'string' | 'number' | 'boolean'; value: unknown }>
  nodes: Array<{ id: string; type: NodeType; position: { x: number; y: number }; data: PersistedNodeData }>
  edges: PersistedEdge[]
  groups: CanvasGroup[]
}

export interface BridgeMessage<T = unknown> { type: string; requestId?: string; payload?: T }

export const nodeLabels: Record<NodeType, string> = {
  colorCondition: '色判断', imageCondition: '图判断', ocrCondition: '文本判断',
  jump: '跳转',
  start: '开始', mouseClick: '点击', mouseMove: '鼠标移动', mouseDrag: '鼠标拖动', mouseScroll: '鼠标滑动', keyPress: '键盘按键',
  textInput: '输入文本', wait: '等待', runProgram: '启动程序', closeProgram: '关闭程序',
  minimizeWindow: '最小化窗口', arrangeWindows: '窗口整理',
  setWindowResolution: '设置窗口分辨率', end: '结束',
}

export const defaultParameters: Record<NodeType, Record<string, unknown>> = {
  colorCondition: { coordinateMode: 'followScript', x: 0, y: 0, width: 1, height: 1, targetColor: '#FFFFFF', tolerance: 10, minMatchRatio: 1 },
  imageCondition: { imageSearchMode: 'fixed', coordinateMode: 'followScript', imageMatchEngine: 'custom', templatePath: '', x: 0, y: 0, tolerance: 10, matchThreshold: 0.95 },
  ocrCondition: { textSearchMode: 'fixed', coordinateMode: 'followScript', ocrEngine: 'gemini', x: 0, y: 0, width: 1, height: 1, targetText: '', matchThreshold: 0.8 },
  jump: { scriptId: '', actionId: '' },
  start: {}, end: {},
  mouseClick: { coordinateMode: 'followScript', x: 500, y: 300, button: 'left', clickCount: 1, intervalMs: 100 },
  mouseMove: { coordinateMode: 'followScript', x: 500, y: 300, durationMs: 0 },
  mouseDrag: { coordinateMode: 'followScript', startX: 500, startY: 300, endX: 700, endY: 300, button: 'left', durationMs: 500 },
  mouseScroll: { coordinateMode: 'followScript', x: 500, y: 300, delta: 120 },
  keyPress: { key: 'A', durationMs: 50, repeatCount: 1, intervalMs: 100 },
  textInput: { text: 'Hello GameFlow', intervalMs: 30 },
  wait: { durationMs: 1000 },
  runProgram: { path: '', arguments: '' },
  closeProgram: { titlePattern: '.*' },
  minimizeWindow: { titlePattern: '.*' },
  arrangeWindows: { titlePattern: '.*', x: 0, y: 0, offsetY: 20 },
  setWindowResolution: { titlePattern: '.*', width: 1280, height: 720 },
}

function normalizedParameters(node: WorkflowDocument['nodes'][number]): Record<string, unknown> {
  return { ...defaultParameters[node.type], ...node.data.parameters }
}

export interface WorkflowGraph {
  nodes: GameFlowNode[]
  edges: Edge[]
}

export function fromDocument(document: WorkflowDocument): WorkflowGraph {
  const groups = document.groups ?? []
  const externalEdges = new Map(groups.flatMap(group => (group.externalEdges ?? []).map(edge => [edge.id, edge] as const)))
  const groupByNode = new Map(groups.flatMap(group => group.nodeIds.map(nodeId => [nodeId, group] as const)))
  const graphNodes: GameFlowNode[] = document.nodes.map(node => {
    const group = groupByNode.get(node.id)
    return {
      id: node.id,
      type: 'gameNode',
      position: { ...node.position },
      hidden: group?.collapsed ?? false,
      data: {
        nodeType: node.type,
        displayName: node.data.displayName,
        parameters: normalizedParameters(node),
      },
    }
  })
  const groupNodes: GameFlowNode[] = groups.map(group => ({
    id: group.id,
    type: 'groupNode',
    position: { ...group.position },
    selectable: true,
    zIndex: -1,
    data: {
      nodeType: 'start',
      displayName: group.name,
      parameters: {},
      group,
    },
  }))
  return {
    nodes: [...groupNodes, ...graphNodes],
    edges: document.edges.map(edge => ({ ...(externalEdges.get(edge.id) ?? edge), type: 'smoothstep' })),
  }
}

export function toDocument(
  source: WorkflowDocument,
  nodes: GameFlowNode[],
  edges: Edge[],
): WorkflowDocument {
  const persistedEdges = edges.map(edge => ({
    id: edge.id,
    source: edge.source,
    sourceHandle: edge.sourceHandle ?? 'output',
    target: edge.target,
    targetHandle: edge.targetHandle ?? 'input',
  }))
  const groups = (source.groups ?? []).map(group => ({
    ...group,
    externalEdges: persistedEdges.filter(edge => edge.source === group.id || edge.target === group.id),
  }))
  const groupById = new Map(groups.map(group => [group.id, group] as const))
  const runtimeEdges = persistedEdges.map(edge => ({
    ...edge,
    source: resolveGroupEndpoint(edge.source, groupById, 'source'),
    target: resolveGroupEndpoint(edge.target, groupById, 'target'),
  }))
  return {
    ...source,
    project: { ...source.project, updatedAt: new Date().toISOString() },
    nodes: nodes.map(node => ({
      id: node.id,
      type: node.data.nodeType,
      position: { ...node.position },
      data: {
        displayName: node.data.displayName,
        parameters: { ...node.data.parameters },
      },
    })).filter(node => !nodes.find(item => item.id === node.id)?.data.group),
    edges: runtimeEdges,
    groups,
  }
}

function resolveGroupEndpoint(
  nodeId: string,
  groups: Map<string, CanvasGroup>,
  side: 'source' | 'target',
): string {
  const group = groups.get(nodeId)
  if (!group || group.nodeIds.length === 0) return nodeId
  return side === 'source' ? group.nodeIds[group.nodeIds.length - 1] : group.nodeIds[0]
}
