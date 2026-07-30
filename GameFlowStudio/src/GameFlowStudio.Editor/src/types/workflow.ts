import type { Edge, Node } from '@xyflow/react'

export const nodeTypes = [
  'start', 'windowScope', 'mouseClick', 'mouseMove', 'mouseDrag', 'mouseScroll', 'keyPress', 'textInput', 'wait',
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
}

export type GameFlowNode = Node<EditorNodeData, 'gameNode'>

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
  schemaVersion: '1.4'
  project: ProjectMetadata
  variables: Record<string, { type: 'string' | 'number' | 'boolean'; value: unknown }>
  nodes: Array<{ id: string; type: NodeType; position: { x: number; y: number }; data: PersistedNodeData }>
  edges: Array<{ id: string; source: string; sourceHandle: string; target: string; targetHandle: string }>
}

export interface BridgeMessage<T = unknown> { type: string; requestId?: string; payload?: T }

export const nodeLabels: Record<NodeType, string> = {
  colorCondition: '色判断', imageCondition: '图判断', ocrCondition: '文本判断',
  jump: '跳转',
  start: '开始', windowScope: '窗口作用域', mouseClick: '点击', mouseMove: '鼠标移动', mouseDrag: '鼠标拖动', mouseScroll: '鼠标滑动', keyPress: '键盘按键',
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
  windowScope: { titlePattern: '.*', outputVariable: 'windowHandles' },
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
  const coordinateNodes: NodeType[] = ['colorCondition', 'imageCondition', 'ocrCondition', 'mouseClick', 'mouseMove', 'mouseDrag', 'mouseScroll']
  const windowProgramNodes: NodeType[] = ['closeProgram', 'minimizeWindow', 'arrangeWindows', 'setWindowResolution']
  if (!coordinateNodes.includes(node.type) && !windowProgramNodes.includes(node.type)) return { ...node.data.parameters }
  const parameters = { ...defaultParameters[node.type], ...node.data.parameters }
  if (windowProgramNodes.includes(node.type)) {
    delete parameters.windowHandleVariable
    delete parameters.windowHandleIndex
    if (node.type === 'arrangeWindows') {
      delete parameters.width
      delete parameters.height
      delete parameters.columns
      delete parameters.gapX
      delete parameters.gapY
    }
    return parameters
  }
  if (parameters.coordinateMode === 'screen') parameters.coordinateMode = 'absolute'
  if (parameters.coordinateMode === 'windowRelative') parameters.coordinateMode = 'relative'
  if (node.type === 'mouseClick') delete parameters.windowHandleIndex
  return parameters
}

export interface WorkflowGraph {
  nodes: GameFlowNode[]
  edges: Edge[]
}

export function fromDocument(document: WorkflowDocument): WorkflowGraph {
  return {
    nodes: document.nodes.map(node => ({
      id: node.id,
      type: 'gameNode',
      position: { ...node.position },
      data: {
        nodeType: node.type,
        displayName: node.data.displayName === 'Gemini OCR判断' ? nodeLabels[node.type] : node.data.displayName,
        parameters: normalizedParameters(node),
      },
    })),
    edges: document.edges.map(edge => ({ ...edge, type: 'smoothstep' })),
  }
}

export function toDocument(
  source: WorkflowDocument,
  nodes: GameFlowNode[],
  edges: Edge[],
): WorkflowDocument {
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
    })),
    edges: edges.map(edge => ({
      id: edge.id,
      source: edge.source,
      sourceHandle: edge.sourceHandle ?? 'output',
      target: edge.target,
      targetHandle: edge.targetHandle ?? 'input',
    })),
  }
}
