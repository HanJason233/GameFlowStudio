import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent as ReactMouseEvent, type PointerEvent as ReactPointerEvent } from 'react'
import {
  Background, BackgroundVariant, ControlButton, Controls, ReactFlow, ReactFlowProvider, useReactFlow,
  type Connection, type Edge, type NodeMouseHandler, type OnSelectionChangeParams,
} from '@xyflow/react'
import { EditorErrorBoundary } from './components/EditorErrorBoundary'
import { useEditorBridge } from './hooks/useEditorBridge'
import { useNodeClipboard } from './hooks/useNodeClipboard'
import { useWorkflowDocument } from './hooks/useWorkflowDocument'
import { GameNodeView } from './nodes/GameNode'
import { GroupNodeView } from './nodes/GroupNode'
import { postBridgeMessage } from './services/bridge'
import type { GameFlowNode } from './types/workflow'

interface ContextMenuState {
  target: 'canvas' | 'node' | 'edge' | 'group'
  targetId: string | null
  x: number
  y: number
}

interface RightSelectionState {
  pointerId: number
  startX: number
  startY: number
  bounds: DOMRect
}

interface SelectionBox {
  left: number
  top: number
  width: number
  height: number
}

function EditorCanvas() {
  const document = useWorkflowDocument()
  const { nodes, edges, selectNodes, selectAllNodes, invertNodeSelection, groupNodes, toggleGroup, ungroup, deleteNodes, deleteEdges } = document
  const flow = useReactFlow<GameFlowNode, Edge>()
  const nodeTypes = useMemo(() => ({ gameNode: GameNodeView, groupNode: GroupNodeView }), [])
  const renderEdges = useMemo(() => edges.map(edge => ({ ...edge, style: { ...edge.style, strokeWidth: 2 } })), [edges])
  const bridgeHandlers = useMemo(() => ({
    exportDocument: document.exportDocument,
    importDocument: document.importDocument,
    addNode: document.addNode,
    updateNodeData: document.updateNodeData,
    updateExecutionStatus: document.updateExecutionStatus,
    clearExecutionState: document.clearExecutionState,
    setReadOnly: document.setReadOnly,
  }), [document.addNode, document.clearExecutionState, document.exportDocument, document.importDocument, document.setReadOnly, document.updateExecutionStatus, document.updateNodeData])

  useEditorBridge(bridgeHandlers)
  const { copyNodes, pasteNodes, hasCopiedNodes } = useNodeClipboard(document.nodes, document.setNodes, document.readOnly, document.markChanged)
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null)
  const [selectionBox, setSelectionBox] = useState<SelectionBox | null>(null)
  const rightSelection = useRef<RightSelectionState | null>(null)

  useEffect(() => {
    const closeContextMenu = () => setContextMenu(null)
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') closeContextMenu()
    }
    window.addEventListener('click', closeContextMenu)
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      window.removeEventListener('click', closeContextMenu)
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [])

  useEffect(() => {
    const onPointerMove = (event: PointerEvent) => {
      const active = rightSelection.current
      if (!active || active.pointerId !== event.pointerId) return
      event.preventDefault()
      const currentX = event.clientX - active.bounds.left
      const currentY = event.clientY - active.bounds.top
      const startX = active.startX - active.bounds.left
      const startY = active.startY - active.bounds.top
      setSelectionBox({
        left: Math.min(startX, currentX),
        top: Math.min(startY, currentY),
        width: Math.abs(currentX - startX),
        height: Math.abs(currentY - startY),
      })
    }

    const onPointerUp = (event: PointerEvent) => {
      const active = rightSelection.current
      if (!active || active.pointerId !== event.pointerId) return
      event.preventDefault()
      rightSelection.current = null
      setSelectionBox(null)

      const left = Math.min(active.startX, event.clientX)
      const top = Math.min(active.startY, event.clientY)
      const width = Math.abs(event.clientX - active.startX)
      const height = Math.abs(event.clientY - active.startY)
      if (width < 4 || height < 4) return

      const start = flow.screenToFlowPosition({ x: left, y: top })
      const end = flow.screenToFlowPosition({ x: left + width, y: top + height })
      const selected = flow.getIntersectingNodes({ x: start.x, y: start.y, width: end.x - start.x, height: end.y - start.y }, false)
        .filter(node => !node.data.group)
      selectNodes(selected.map(node => node.id))
    }

    window.addEventListener('pointermove', onPointerMove, { passive: false })
    window.addEventListener('pointerup', onPointerUp, { passive: false })
    window.addEventListener('pointercancel', onPointerUp, { passive: false })
    return () => {
      window.removeEventListener('pointermove', onPointerMove)
      window.removeEventListener('pointerup', onPointerUp)
      window.removeEventListener('pointercancel', onPointerUp)
    }
  }, [flow, selectNodes])

  const isValidConnection = useCallback((connection: Connection | Edge) => {
    const source = nodes.find(node => node.id === connection.source)
    const target = nodes.find(node => node.id === connection.target)
    if (!source || !target || source.id === target.id) return false
    if ((!source.data.group && source.data.nodeType === 'end')
      || (!target.data.group && target.data.nodeType === 'start')) return false
    const currentEdgeId = 'id' in connection ? connection.id : undefined
    return !edges.some(edge =>
      edge.id !== currentEdgeId
      && edge.source === connection.source
      && edge.sourceHandle === connection.sourceHandle,
    )
  }, [edges, nodes])

  const onSelectionChange = useCallback(({ nodes }: OnSelectionChangeParams<GameFlowNode, Edge>) => {
    const realNodes = nodes.filter(node => !node.data.group)
    const selection = realNodes.length === 1 ? { nodeId: realNodes[0].id, nodeType: realNodes[0].data.nodeType, data: realNodes[0].data } : null
    postBridgeMessage('node_selected', selection)
  }, [])

  const onNodeContextMenu: NodeMouseHandler<GameFlowNode> = useCallback((event, node) => {
    event.preventDefault()
    event.stopPropagation()
    if (node.data.group) {
      setContextMenu({ target: 'group', targetId: node.id, x: event.clientX, y: event.clientY })
      return
    }
    if (!node.selected) selectNodes([node.id])
    setContextMenu({ target: 'node', targetId: node.id, x: event.clientX, y: event.clientY })
  }, [selectNodes])

  const onEdgeContextMenu = useCallback((event: ReactMouseEvent, edge: Edge) => {
    event.preventDefault()
    event.stopPropagation()
    setContextMenu({ target: 'edge', targetId: edge.id, x: event.clientX, y: event.clientY })
  }, [])

  const onEditorPointerDownCapture = useCallback((event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 2 || event.pointerType !== 'mouse') return
    const target = event.target instanceof Element ? event.target : null
    if (target?.closest('.react-flow__node, .react-flow__edge, .react-flow__controls, .flow-toolbar, .node-context-menu, .empty-hint')) return

    event.preventDefault()
    event.stopPropagation()
    rightSelection.current = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      bounds: event.currentTarget.getBoundingClientRect(),
    }
    const bounds = rightSelection.current.bounds
    setSelectionBox({ left: event.clientX - bounds.left, top: event.clientY - bounds.top, width: 0, height: 0 })
    setContextMenu(null)
  }, [])

  const onPaneContextMenu = useCallback((event: ReactMouseEvent | globalThis.MouseEvent) => {
    event.preventDefault()
    setContextMenu({ target: 'canvas', targetId: null, x: event.clientX, y: event.clientY })
  }, [])

  const selectedNodeIds = useMemo(() => nodes.filter(node => node.selected && !node.data.group).map(node => node.id), [nodes])

  const copyContextNode = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'node' || contextMenu.targetId === null) return
    copyNodes([contextMenu.targetId])
    setContextMenu(null)
  }, [contextMenu, copyNodes])

  const cutContextNode = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'node' || contextMenu.targetId === null) return
    const node = nodes.find(item => item.id === contextMenu.targetId)
    const ids = node?.selected && selectedNodeIds.length > 0 ? selectedNodeIds : [contextMenu.targetId]
    copyNodes(ids)
    deleteNodes(ids)
    setContextMenu(null)
  }, [contextMenu, copyNodes, deleteNodes, nodes, selectedNodeIds])

  const deleteContextNode = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'node' || contextMenu.targetId === null) return
    deleteNodes([contextMenu.targetId])
    setContextMenu(null)
  }, [contextMenu, deleteNodes])

  const copySelectedNodes = useCallback(() => {
    if (selectedNodeIds.length === 0) return
    copyNodes(selectedNodeIds)
    setContextMenu(null)
  }, [copyNodes, selectedNodeIds])

  const cutSelectedNodes = useCallback(() => {
    if (selectedNodeIds.length === 0) return
    copyNodes(selectedNodeIds)
    deleteNodes(selectedNodeIds)
    setContextMenu(null)
  }, [copyNodes, deleteNodes, selectedNodeIds])

  const deleteSelectedNodes = useCallback(() => {
    if (selectedNodeIds.length === 0) return
    deleteNodes(selectedNodeIds)
    setContextMenu(null)
  }, [deleteNodes, selectedNodeIds])

  const deleteContextEdge = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'edge' || contextMenu.targetId === null) return
    deleteEdges([contextMenu.targetId])
    setContextMenu(null)
  }, [contextMenu, deleteEdges])

  const openContextProperties = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'node' || contextMenu.targetId === null) return
    const node = nodes.find(item => item.id === contextMenu.targetId)
    if (!node) return
    selectNodes([node.id])
    postBridgeMessage('node_context_menu', {
      nodeId: node.id,
      nodeType: node.data.nodeType,
      data: node.data,
    })
    setContextMenu(null)
  }, [contextMenu, nodes, selectNodes])

  const pasteContextNodes = useCallback(() => {
    if (!contextMenu || contextMenu.target !== 'canvas') return
    pasteNodes()
    setContextMenu(null)
  }, [contextMenu, pasteNodes])

  const groupSelectedNodes = useCallback(() => {
    groupNodes(selectedNodeIds)
    setContextMenu(null)
  }, [groupNodes, selectedNodeIds])

  const toggleContextGroup = useCallback(() => {
    if (!contextMenu?.targetId) return
    toggleGroup(contextMenu.targetId)
    setContextMenu(null)
  }, [contextMenu, toggleGroup])

  const ungroupContextGroup = useCallback(() => {
    if (!contextMenu?.targetId) return
    ungroup(contextMenu.targetId)
    setContextMenu(null)
  }, [contextMenu, ungroup])

  const contextMenuIsValid = contextMenu !== null
    && (contextMenu.target === 'canvas'
      || (contextMenu.target === 'node' && contextMenu.targetId !== null && nodes.some(node => node.id === contextMenu.targetId))
      || (contextMenu.target === 'group' && contextMenu.targetId !== null && nodes.some(node => node.id === contextMenu.targetId && !!node.data.group))
      || (contextMenu.target === 'edge' && contextMenu.targetId !== null && edges.some(edge => edge.id === contextMenu.targetId)))

  return <div className="editor" onPointerDownCapture={onEditorPointerDownCapture} onClick={() => setContextMenu(null)} onContextMenu={event => event.preventDefault()}>
    {document.nodes.length === 0 && <div className="empty-hint"><div><strong>从左侧工具箱添加节点</strong><br />连接节点后即可保存并运行自动化流程</div></div>}
    <ReactFlow
      nodes={document.nodes} edges={renderEdges} nodeTypes={nodeTypes}
      onNodesChange={document.onNodesChange} onEdgesChange={document.onEdgesChange}
      onConnect={document.onConnect} isValidConnection={isValidConnection} onSelectionChange={onSelectionChange}
      onNodeContextMenu={onNodeContextMenu} onEdgeContextMenu={onEdgeContextMenu} onPaneContextMenu={onPaneContextMenu}
      nodesDraggable={!document.readOnly} nodesConnectable={!document.readOnly} edgesReconnectable={!document.readOnly}
      deleteKeyCode={document.readOnly ? null : ['Backspace', 'Delete']}
      proOptions={{ hideAttribution: true }}
      minZoom={.2} maxZoom={1.5}>
      <Background variant={BackgroundVariant.Dots} gap={22} size={1.2} color="var(--canvas-grid)" />
      <Controls showInteractive={false} position="bottom-left">
        <ControlButton className="organize-control-button" onClick={document.arrangeNodes} disabled={document.readOnly || document.nodes.length < 2} aria-label="紧凑整理流程" title="紧凑整理并聚焦流程">
          <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
            <rect x="3" y="3" width="6" height="5" rx="1" />
            <rect x="15" y="3" width="6" height="5" rx="1" />
            <rect x="9" y="16" width="6" height="5" rx="1" />
            <path d="M9 5.5h6M6 8v4c0 .8.7 1.5 1.5 1.5h4.5M18 8v4c0 .8-.7 1.5-1.5 1.5H12" />
          </svg>
        </ControlButton>
      </Controls>
    </ReactFlow>
    {selectionBox && <div className="right-selection-box" style={selectionBox} aria-hidden="true" />}
    {contextMenuIsValid && contextMenu && <div className="node-context-menu" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={event => event.stopPropagation()} onContextMenu={event => event.preventDefault()}>
      {contextMenu.target === 'canvas' ? (
        <>
          <button type="button" onClick={cutSelectedNodes} disabled={document.readOnly || selectedNodeIds.length === 0}>剪切</button>
          <button type="button" onClick={copySelectedNodes} disabled={document.readOnly || selectedNodeIds.length === 0}>复制</button>
          <button type="button" onClick={pasteContextNodes} disabled={document.readOnly || !hasCopiedNodes}>粘贴</button>
          <button type="button" onClick={deleteSelectedNodes} disabled={document.readOnly || selectedNodeIds.length === 0}>删除</button>
          <button type="button" onClick={selectAllNodes} disabled={document.readOnly}>全选</button>
          <button type="button" onClick={invertNodeSelection} disabled={document.readOnly}>反选</button>
          <button type="button" onClick={groupSelectedNodes} disabled={document.readOnly || selectedNodeIds.length < 2}>折叠</button>
        </>
      ) : contextMenu.target === 'edge' ? (
        <button type="button" onClick={deleteContextEdge} disabled={document.readOnly}>删除</button>
      ) : contextMenu.target === 'group' ? (
        <>
          <button type="button" onClick={toggleContextGroup} disabled={document.readOnly}>展开/折叠卡片组</button>
          <button type="button" onClick={ungroupContextGroup} disabled={document.readOnly}>取消卡片组</button>
        </>
      ) : (
        <>
          <button type="button" onClick={cutContextNode} disabled={document.readOnly}>剪切</button>
          <button type="button" onClick={copyContextNode} disabled={document.readOnly}>复制</button>
          <button type="button" onClick={deleteContextNode} disabled={document.readOnly}>删除</button>
          <button type="button" onClick={openContextProperties}>属性</button>
          <button type="button" onClick={groupSelectedNodes} disabled={document.readOnly || selectedNodeIds.length < 2}>折叠</button>
        </>
      )}
    </div>}
  </div>
}

export default function App() {
  return <EditorErrorBoundary><ReactFlowProvider><EditorCanvas /></ReactFlowProvider></EditorErrorBoundary>
}
