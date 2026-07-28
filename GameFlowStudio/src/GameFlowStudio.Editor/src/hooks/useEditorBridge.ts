import { useEffect, useLayoutEffect, useRef } from 'react'
import { postBridgeMessage, subscribeBridge } from '../services/bridge'
import type { BridgeMessage, NodeDataUpdate, NodeType, WorkflowDocument } from '../types/workflow'

interface EditorBridgeHandlers {
  exportDocument: () => WorkflowDocument
  importDocument: (document: WorkflowDocument) => void
  addNode: (nodeType: NodeType) => void
  updateNodeData: (nodeId: string, data: NodeDataUpdate) => void
  updateExecutionStatus: (nodeId: string, status: 'running' | 'success' | 'error') => void
  clearExecutionState: () => void
  setReadOnly: (readOnly: boolean) => void
}

export function useEditorBridge(handlers: EditorBridgeHandlers): void {
  const handlersRef = useRef(handlers)
  useLayoutEffect(() => {
    handlersRef.current = handlers
  }, [handlers])

  useEffect(() => {
    const unsubscribe = subscribeBridge((message: BridgeMessage) => routeMessage(message, handlersRef.current))
    postBridgeMessage('editor_ready')
    return unsubscribe
  }, [])
}

function routeMessage(message: BridgeMessage, handlers: EditorBridgeHandlers): void {
  switch (message.type) {
    case 'initialize_editor': case 'import_workflow':
      handlers.importDocument(message.payload as WorkflowDocument); break
    case 'request_workflow_export':
      postBridgeMessage('workflow_exported', handlers.exportDocument(), message.requestId); break
    case 'add_node':
      handlers.addNode((message.payload as { nodeType: NodeType }).nodeType); break
    case 'update_node_data': {
      const update = message.payload as { nodeId: string; data: NodeDataUpdate }
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
