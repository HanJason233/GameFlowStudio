import { useCallback, useState } from 'react'
import {
  addEdge, useEdgesState, useNodesState, useReactFlow,
  type Connection, type Edge, type EdgeChange, type NodeChange,
} from '@xyflow/react'
import { postBridgeMessage } from '../services/bridge'
import { arrangeWorkflowNodes } from '../workflowLayout'
import {
  defaultParameters, fromDocument, nodeLabels, toDocument,
  nextActionId, type GameFlowNode, type NodeDataUpdate, type NodeType, type WorkflowDocument,
} from '../types/workflow'

const createEmptyDocument = (): WorkflowDocument => ({
  schemaVersion: '1.4',
  project: { id: `project-${crypto.randomUUID()}`, name: '未命名项目', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() },
  variables: {}, nodes: [], edges: [],
})

export function useWorkflowDocument() {
  const [sourceDocument, setSourceDocument] = useState<WorkflowDocument>(createEmptyDocument)
  const [nodes, setNodes, applyNodeChanges] = useNodesState<GameFlowNode>([])
  const [edges, setEdges, applyEdgeChanges] = useEdgesState<Edge>([])
  const [readOnly, setReadOnly] = useState(false)
  const flow = useReactFlow<GameFlowNode, Edge>()

  const exportDocument = useCallback(
    (): WorkflowDocument => toDocument(sourceDocument, nodes, edges),
    [edges, nodes, sourceDocument],
  )

  const markChanged = useCallback(() => postBridgeMessage('workflow_changed'), [])

  const importDocument = useCallback((incoming: WorkflowDocument) => {
    const graph = fromDocument(incoming)
    setSourceDocument(incoming)
    setNodes(graph.nodes)
    setEdges(graph.edges)
    setTimeout(() => flow.fitView({ padding: .2 }), 30)
  }, [flow, setEdges, setNodes])

  const addNode = useCallback((nodeType: NodeType) => {
    if (readOnly) return
    const position = flow.screenToFlowPosition({ x: window.innerWidth / 2, y: window.innerHeight / 2 })
    setNodes(current => [...current, { id: nextActionId(current), type: 'gameNode', position, data: { nodeType, displayName: nodeLabels[nodeType], parameters: structuredClone(defaultParameters[nodeType]) } }])
    markChanged()
  }, [flow, markChanged, readOnly, setNodes])

  const updateNodeData = useCallback((nodeId: string, data: NodeDataUpdate) => {
    setNodes(current => current.map(node => node.id === nodeId ? { ...node, data: { ...node.data, ...data, parameters: { ...node.data.parameters, ...data.parameters } } } : node))
    markChanged()
  }, [markChanged, setNodes])

  const updateExecutionStatus = useCallback((nodeId: string, status: 'running' | 'success' | 'error') => {
    setNodes(current => current.map(node => {
      if (node.id === nodeId) return { ...node, data: { ...node.data, executionStatus: status } }
      if (status === 'running' && node.data.executionStatus === 'running') return { ...node, data: { ...node.data, executionStatus: 'none' } }
      return node
    }))
  }, [setNodes])

  const clearExecutionState = useCallback(() => setNodes(current => current.map(node => ({ ...node, data: { ...node.data, executionStatus: 'none' } }))), [setNodes])

  const onConnect = useCallback((connection: Connection) => {
    if (readOnly) return
    setEdges(current => addEdge({ ...connection, id: `edge-${crypto.randomUUID()}`, type: 'smoothstep' }, current))
    markChanged()
  }, [markChanged, readOnly, setEdges])

  const onNodesChange = useCallback((changes: NodeChange<GameFlowNode>[]) => {
    const allowed = readOnly ? changes.filter(isReadOnlyNodeChange) : changes
    applyNodeChanges(allowed)
    if (!readOnly && changes.some(isPersistentNodeChange)) markChanged()
  }, [applyNodeChanges, markChanged, readOnly])

  const onEdgesChange = useCallback((changes: EdgeChange<Edge>[]) => {
    if (readOnly) return
    applyEdgeChanges(changes)
    if (changes.some(isPersistentEdgeChange)) markChanged()
  }, [applyEdgeChanges, markChanged, readOnly])

  const arrangeNodes = useCallback(() => {
    if (readOnly || nodes.length < 2) return
    setNodes(arrangeWorkflowNodes(nodes, edges))
    markChanged()
    setTimeout(() => flow.fitView({ padding: .2, duration: 260 }), 30)
  }, [edges, flow, markChanged, nodes, readOnly, setNodes])

  return {
    nodes, edges, readOnly, setNodes, setReadOnly, exportDocument, importDocument, addNode,
    updateNodeData, updateExecutionStatus, clearExecutionState, markChanged,
    onConnect, onNodesChange, onEdgesChange, arrangeNodes,
  }
}

function isReadOnlyNodeChange(change: NodeChange<GameFlowNode>): boolean {
  return change.type === 'select' || change.type === 'dimensions'
}

function isPersistentNodeChange(change: NodeChange<GameFlowNode>): boolean {
  return change.type === 'add'
    || change.type === 'remove'
    || change.type === 'replace'
    || (change.type === 'position' && change.dragging !== true)
}

function isPersistentEdgeChange(change: EdgeChange<Edge>): boolean {
  return change.type === 'add' || change.type === 'remove' || change.type === 'replace'
}
