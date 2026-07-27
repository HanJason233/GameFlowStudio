import type { Edge, Node } from '@xyflow/react'

export type NodeType = 'start' | 'windowScope' | 'mouseClick' | 'mouseMove' | 'mouseDrag' | 'mouseScroll' | 'keyPress' | 'textInput' | 'wait' |
  'colorCondition' | 'imageCondition' | 'ocrCondition' | 'stateCondition' | 'jump' | 'runProgram' | 'closeProgram' |
  'minimizeWindow' | 'arrangeWindows' | 'setWindowResolution' | 'end'

export type ExecutionStatus = 'none' | 'running' | 'success' | 'error'

export interface GameNodeData extends Record<string, unknown> {
  displayName: string
  parameters: Record<string, unknown>
  executionStatus?: ExecutionStatus
}

export type GameFlowNode = Node<GameNodeData, 'gameNode'>

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

export interface AutomationProject {
  schemaVersion: '1.4'
  project: ProjectMetadata
  variables: Record<string, { type: 'string' | 'number' | 'boolean'; value: unknown }>
  nodes: Array<{ id: string; type: NodeType; position: { x: number; y: number }; data: GameNodeData }>
  edges: Array<{ id: string; source: string; sourceHandle: string; target: string; targetHandle: string }>
}

export interface BridgeMessage<T = unknown> { type: string; requestId?: string; payload?: T }

export const nodeLabels: Record<NodeType, string> = {
  colorCondition: '色判断', imageCondition: '图判断', ocrCondition: 'OCR判断',
  stateCondition: '联合判断', jump: '跳转',
  start: '开始', windowScope: '窗口作用域', mouseClick: '点击', mouseMove: '鼠标移动', mouseDrag: '鼠标拖动', mouseScroll: '鼠标滑动', keyPress: '键盘按键',
  textInput: '输入文本', wait: '等待', runProgram: '启动程序', closeProgram: '关闭程序',
  minimizeWindow: '最小化窗口', arrangeWindows: '窗口整理',
  setWindowResolution: '设置窗口分辨率', end: '结束',
}

export const defaultParameters: Record<NodeType, Record<string, unknown>> = {
  colorCondition: { coordinateMode: 'followScript', x: 0, y: 0, width: 1, height: 1, targetColor: '#FFFFFF', tolerance: 10, minMatchRatio: 1 },
  imageCondition: { coordinateMode: 'followScript', templatePath: '', x: 0, y: 0, tolerance: 10, matchThreshold: 0.95 },
  ocrCondition: { coordinateMode: 'followScript', x: 0, y: 0, width: 1, height: 1, targetText: '', matchThreshold: 0.8 },
  stateCondition: { detectors: [] },
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
  runProgram: { path: '', arguments: '', workingDirectory: '', waitForExit: false },
  closeProgram: { windowHandleVariable: 'windowHandles', windowHandleIndex: 0 },
  minimizeWindow: { windowHandleVariable: 'windowHandles', windowHandleIndex: 0 },
  arrangeWindows: { windowHandleVariable: 'windowHandles', x: 0, y: 0, width: 800, height: 600, columns: 2, gapX: 20, gapY: 20 },
  setWindowResolution: { windowHandleVariable: 'windowHandles', windowHandleIndex: 0, width: 1280, height: 720 },
}

function normalizedParameters(node: AutomationProject['nodes'][number]): Record<string, unknown> {
  const coordinateNodes: NodeType[] = ['colorCondition', 'imageCondition', 'ocrCondition', 'mouseClick', 'mouseMove', 'mouseDrag', 'mouseScroll']
  if (!coordinateNodes.includes(node.type)) return node.data.parameters
  const parameters = { ...defaultParameters[node.type], ...node.data.parameters }
  if (parameters.coordinateMode === 'screen') parameters.coordinateMode = 'absolute'
  if (parameters.coordinateMode === 'windowRelative') parameters.coordinateMode = 'relative'
  if (node.type === 'mouseClick') {
    delete parameters.windowHandleVariable
    delete parameters.windowHandleIndex
  }
  return parameters
}

export function toFlowNodes(project: AutomationProject): GameFlowNode[] {
  return project.nodes.map(node => ({ id: node.id, type: 'gameNode', position: node.position, data: { ...node.data, parameters: normalizedParameters(node) } }))
}

export function toFlowEdges(project: AutomationProject): Edge[] {
  return project.edges.map(edge => ({ ...edge, type: 'smoothstep' }))
}
