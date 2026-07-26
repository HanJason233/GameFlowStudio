import { useCallback, useMemo, type MouseEvent as ReactMouseEvent } from 'react'
import {
  Background, BackgroundVariant, Controls, ReactFlow, ReactFlowProvider,
  type Connection, type Edge, type NodeMouseHandler, type OnSelectionChangeParams,
} from '@xyflow/react'
import { useEditorBridge } from './hooks/useEditorBridge'
import { useNodeClipboard } from './hooks/useNodeClipboard'
import { useWorkflowDocument } from './hooks/useWorkflowDocument'
import { GameNodeView } from './nodes/GameNode'
import { postBridgeMessage } from './services/bridge'
import type { GameFlowNode } from './types/workflow'

function EditorCanvas() {
  const document = useWorkflowDocument()
  const nodeTypes = useMemo(() => ({ gameNode: GameNodeView }), [])
  const bridgeHandlers = useMemo(() => ({
    exportProject: document.exportProject,
    importProject: document.importProject,
    addNode: document.addNode,
    updateNodeData: document.updateNodeData,
    updateExecutionStatus: document.updateExecutionStatus,
    clearExecutionState: document.clearExecutionState,
    setReadOnly: document.setReadOnly,
  }), [document.addNode, document.clearExecutionState, document.exportProject, document.importProject, document.setReadOnly, document.updateExecutionStatus, document.updateNodeData])

  useEditorBridge(bridgeHandlers)
  useNodeClipboard(document.nodes, document.setNodes, document.readOnly, document.notifyChanged)

  const isValidConnection = useCallback((connection: Connection | Edge) => {
    const source = document.nodes.find(node => node.id === connection.source)
    const target = document.nodes.find(node => node.id === connection.target)
    if (!source || !target || source.id === target.id) return false
    if (source.data.nodeType === 'end' || target.data.nodeType === 'start') return false
    return !document.edges.some(edge => edge.source === connection.source && edge.sourceHandle === connection.sourceHandle)
  }, [document.edges, document.nodes])

  const onSelectionChange = useCallback(({ nodes }: OnSelectionChangeParams<GameFlowNode, Edge>) => {
    const selection = nodes.length === 1 ? { nodeId: nodes[0].id, nodeType: nodes[0].data.nodeType, data: nodes[0].data } : null
    postBridgeMessage('node_selected', selection)
  }, [])

  const onNodeContextMenu: NodeMouseHandler<GameFlowNode> = useCallback((event, node) => {
    event.preventDefault()
    event.stopPropagation()
    postBridgeMessage('node_context_menu', {
      nodeId: node.id,
      nodeType: node.data.nodeType,
      data: node.data,
    })
  }, [])

  const onPaneContextMenu = useCallback((event: ReactMouseEvent | globalThis.MouseEvent) => {
    event.preventDefault()
  }, [])

  return <div className="editor" onContextMenu={event => event.preventDefault()}>
    {document.nodes.length === 0 && <div className="empty-hint"><div><strong>从左侧工具箱添加节点</strong><br />连接节点后即可保存并运行自动化流程</div></div>}
    <ReactFlow
      nodes={document.nodes} edges={document.edges} nodeTypes={nodeTypes}
      onNodesChange={document.onNodesChange} onEdgesChange={document.onEdgesChange}
      onConnect={document.onConnect} isValidConnection={isValidConnection} onSelectionChange={onSelectionChange}
      onNodeContextMenu={onNodeContextMenu} onPaneContextMenu={onPaneContextMenu}
      nodesDraggable={!document.readOnly} nodesConnectable={!document.readOnly} edgesReconnectable={!document.readOnly}
      deleteKeyCode={document.readOnly ? null : ['Backspace', 'Delete']} fitView>
      <Background variant={BackgroundVariant.Dots} gap={22} size={1.2} color="#4b6386" />
      <Controls showInteractive={false} />
    </ReactFlow>
  </div>
}

export default function App() { return <ReactFlowProvider><EditorCanvas /></ReactFlowProvider> }
