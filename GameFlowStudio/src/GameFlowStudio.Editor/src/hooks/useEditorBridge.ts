import { useEffect } from 'react'
import { postBridgeMessage, subscribeBridge } from '../services/bridge'
import type { AutomationProject, BridgeMessage, GameFlowNode, NodeType } from '../types/workflow'

interface EditorBridgeHandlers {
  exportProject: () => AutomationProject
  importProject: (project: AutomationProject) => void
  addNode: (nodeType: NodeType) => void
  updateNodeData: (nodeId: string, data: Partial<GameFlowNode['data']>) => void
  updateExecutionStatus: (nodeId: string, status: 'running' | 'success' | 'error') => void
  clearExecutionState: () => void
  setReadOnly: (readOnly: boolean) => void
}

export function useEditorBridge(handlers: EditorBridgeHandlers): void {
  useEffect(() => subscribeBridge((message: BridgeMessage) => routeMessage(message, handlers)), [handlers])
  useEffect(() => { postBridgeMessage('editor_ready') }, [])
}

function routeMessage(message: BridgeMessage, handlers: EditorBridgeHandlers): void {
  switch (message.type) {
    case 'initialize_editor': case 'import_workflow':
      handlers.importProject(message.payload as AutomationProject); break
    case 'request_workflow_export':
      postBridgeMessage('workflow_exported', handlers.exportProject(), message.requestId); break
    case 'add_node':
      handlers.addNode((message.payload as { nodeType: NodeType }).nodeType); break
    case 'update_node_data': {
      const update = message.payload as { nodeId: string; data: Partial<GameFlowNode['data']> }
      handlers.updateNodeData(update.nodeId, update.data); break
    }
    case 'highlight_node': handlers.updateExecutionStatus(nodeId(message), 'running'); break
    case 'mark_node_success': handlers.updateExecutionStatus(nodeId(message), 'success'); break
    case 'mark_node_error': handlers.updateExecutionStatus(nodeId(message), 'error'); break
    case 'clear_execution_state': handlers.clearExecutionState(); break
    case 'set_editor_readonly': handlers.setReadOnly((message.payload as { readOnly: boolean }).readOnly); break
  }
}

function nodeId(message: BridgeMessage): string { return (message.payload as { nodeId: string }).nodeId }
