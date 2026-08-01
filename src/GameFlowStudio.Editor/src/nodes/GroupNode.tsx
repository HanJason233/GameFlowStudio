import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { GameFlowNode } from '../types/workflow'

export function GroupNodeView({ data, selected }: NodeProps<GameFlowNode>) {
  const group = data.group
  if (!group) return null
  return <div className={`game-node group-action-node ${selected ? 'selected' : ''}`} aria-label={`卡片组：${group.name}`}>
    <Handle type="target" id="input" position={Position.Left} />
    <div className="node-heading">{group.name}</div>
    <div className="node-type">折叠动作组 · {group.nodeIds.length} 个动作</div>
    <Handle type="source" id="output" position={Position.Right} />
  </div>
}
