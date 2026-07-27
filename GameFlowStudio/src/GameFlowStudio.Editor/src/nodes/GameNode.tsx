import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { GameFlowNode, NodeType } from '../types/workflow'
import { nodeLabels } from '../types/workflow'

const colors: Record<NodeType, string> = {
  start: '#22c55e', end: '#ef4444', colorCondition: '#f59e0b', imageCondition: '#fb923c', ocrCondition: '#c084fc', stateCondition: '#f59e0b', jump: '#a855f7',
  windowScope: '#eab308',
  mouseClick: '#3b82f6', mouseMove: '#0ea5e9', mouseDrag: '#2563eb', mouseScroll: '#0891b2', keyPress: '#6366f1', textInput: '#14b8a6',
  wait: '#64748b', runProgram: '#f97316', closeProgram: '#ef4444',
  minimizeWindow: '#64748b', arrangeWindows: '#14b8a6', setWindowResolution: '#0ea5e9',
}

export function GameNodeView({ data, selected }: NodeProps<GameFlowNode>) {
  const nodeType = (data.nodeType as NodeType | undefined) ?? 'wait'
  const status = data.executionStatus ?? 'none'
  const isCondition = nodeType === 'colorCondition' || nodeType === 'imageCondition' || nodeType === 'ocrCondition' || nodeType === 'stateCondition'
  const outputs = isCondition ? ['true', 'false'] : nodeType === 'end' ? [] : ['output']
  const detectors = nodeType === 'stateCondition' && Array.isArray(data.parameters?.detectors)
    ? data.parameters.detectors
    : []
  const detectorSummary = detectors.length === 0
    ? '未配置检测器'
    : `${detectors.length} 个检测器 · 全部命中`
  return <div className={`game-node status-${status} ${selected ? 'selected' : ''}`} style={{ '--node-color': colors[nodeType] } as React.CSSProperties}>
    {nodeType !== 'start' && <Handle type="target" id="input" position={Position.Left} />}
    <div className="node-heading">{data.displayName || nodeLabels[nodeType]}</div>
    <div className="node-type">{nodeLabels[nodeType]}</div>
    {nodeType === 'stateCondition' && <div className="node-rule-summary">{detectorSummary}</div>}
    {outputs.map((handle, index) => <Handle key={handle} type="source" id={handle} position={Position.Right} style={{ top: outputs.length === 1 ? '50%' : `${35 + index * 30}%` }}>
      {outputs.length > 1 && <span className="handle-label">{handle}</span>}
    </Handle>)}
  </div>
}
