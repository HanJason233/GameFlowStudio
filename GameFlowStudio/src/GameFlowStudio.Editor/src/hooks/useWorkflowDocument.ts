import { useCallback, useState } from 'react'
import {
  addEdge, useEdgesState, useNodesState, useReactFlow,
  type Connection, type Edge, type EdgeChange, type NodeChange,
} from '@xyflow/react'
import { postBridgeMessage } from '../services/bridge'
import { arrangeWorkflowNodes } from '../workflowLayout'
import {
  defaultParameters, nodeLabels, toFlowEdges, toFlowNodes,
  nextActionId, type AutomationProject, type GameFlowNode, type NodeType,
} from '../types/workflow'

const createEmptyProject = (): AutomationProject => ({
  schemaVersion: '1.4',
  project: { id: `project-${crypto.randomUUID()}`, name: '未命名项目', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() },
  variables: {}, nodes: [], edges: [],
})

export function useWorkflowDocument() {
  const [project, setProject] = useState<AutomationProject>(createEmptyProject)
  const [nodes, setNodes, applyNodeChanges] = useNodesState<GameFlowNode>([])
  const [edges, setEdges, applyEdgeChanges] = useEdgesState<Edge>([])
  const [readOnly, setReadOnly] = useState(false)
  const flow = useReactFlow<GameFlowNode, Edge>()

  const exportProject = useCallback((): AutomationProject => ({
    ...project,
    project: { ...project.project, updatedAt: new Date().toISOString() },
    nodes: nodes.map(node => ({ id: node.id, type: node.data.nodeType as NodeType, position: node.position, data: { displayName: node.data.displayName, parameters: node.data.parameters } })),
    edges: edges.map(edge => ({ id: edge.id, source: edge.source, sourceHandle: edge.sourceHandle ?? 'output', target: edge.target, targetHandle: edge.targetHandle ?? 'input' })),
  }), [edges, nodes, project])

  const notifyChanged = useCallback(() => queueMicrotask(() => postBridgeMessage('workflow_changed', exportProject())), [exportProject])

  const importProject = useCallback((incoming: AutomationProject) => {
    setProject(incoming)
    setNodes(toFlowNodes(incoming).map(node => ({ ...node, data: { ...node.data, nodeType: incoming.nodes.find(item => item.id === node.id)?.type } })))
    setEdges(toFlowEdges(incoming))
    setTimeout(() => flow.fitView({ padding: .2 }), 30)
  }, [flow, setEdges, setNodes])

  const addNode = useCallback((nodeType: NodeType) => {
    if (readOnly) return
    const position = flow.screenToFlowPosition({ x: window.innerWidth / 2, y: window.innerHeight / 2 })
    setNodes(current => [...current, { id: nextActionId(current), type: 'gameNode', position, data: { nodeType, displayName: nodeLabels[nodeType], parameters: structuredClone(defaultParameters[nodeType]) } }])
    setTimeout(notifyChanged, 0)
  }, [flow, notifyChanged, readOnly, setNodes])

  const updateNodeData = useCallback((nodeId: string, data: Partial<GameFlowNode['data']>) => {
    setNodes(current => current.map(node => node.id === nodeId ? { ...node, data: { ...node.data, ...data, parameters: { ...node.data.parameters, ...data.parameters } } } : node))
    setTimeout(notifyChanged, 0)
  }, [notifyChanged, setNodes])

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
    setTimeout(notifyChanged, 0)
  }, [notifyChanged, readOnly, setEdges])

  const onNodesChange = useCallback((changes: NodeChange<GameFlowNode>[]) => {
    const allowed = readOnly ? changes.filter(change => change.type === 'select') : changes
    applyNodeChanges(allowed)
    if (!readOnly && changes.some(change => change.type !== 'select')) notifyChanged()
  }, [applyNodeChanges, notifyChanged, readOnly])

  const onEdgesChange = useCallback((changes: EdgeChange<Edge>[]) => {
    if (readOnly) return
    applyEdgeChanges(changes)
    notifyChanged()
  }, [applyEdgeChanges, notifyChanged, readOnly])

  const arrangeNodes = useCallback(() => {
    if (readOnly || nodes.length < 2) return
    setNodes(arrangeWorkflowNodes(nodes, edges))
    setTimeout(() => {
      postBridgeMessage('workflow_changed')
      flow.fitView({ padding: .2, duration: 260 })
    }, 30)
  }, [edges, flow, nodes, readOnly, setNodes])

  return {
    nodes, edges, readOnly, setNodes, setReadOnly, exportProject, importProject, addNode,
    updateNodeData, updateExecutionStatus, clearExecutionState, notifyChanged,
    onConnect, onNodesChange, onEdgesChange, arrangeNodes,
  }
}
