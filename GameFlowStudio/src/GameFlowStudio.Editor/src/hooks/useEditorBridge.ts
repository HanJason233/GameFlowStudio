import { useEffect, useLayoutEffect, useRef } from 'react'
import { postBridgeMessage, subscribeBridge } from '../services/bridge'
import type { BridgeMessage, NodeDataUpdate, NodeType, WorkflowDocument } from '../types/workflow'
import { isNodeDataUpdate, isNodeType, isWorkflowDocument, readRecord } from '../types/workflowGuards'

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
    case 'initialize_editor': case 'import_workflow': {
      if (!isWorkflowDocument(message.payload)) return reject(message.type, 'invalid workflow document')
      handlers.importDocument(message.payload)
      break
    }
    case 'request_workflow_export':
      if (!message.requestId) return reject(message.type, 'missing request id')
      postBridgeMessage('workflow_exported', handlers.exportDocument(), message.requestId); break
    case 'add_node': {
      const payload = readRecord(message.payload)
      if (!payload || !isNodeType(payload.nodeType)) return reject(message.type, 'invalid node type')
      handlers.addNode(payload.nodeType)
      break
    }
    case 'update_node_data': {
      const payload = readRecord(message.payload)
      if (!payload || !isNonEmptyString(payload.nodeId) || !isNodeDataUpdate(payload.data))
        return reject(message.type, 'invalid node update')
      handlers.updateNodeData(payload.nodeId, payload.data)
      break
    }
    case 'highlight_node': routeExecutionStatus(message, handlers, 'running'); break
    case 'mark_node_success': routeExecutionStatus(message, handlers, 'success'); break
    case 'mark_node_error': routeExecutionStatus(message, handlers, 'error'); break
    case 'clear_execution_state': handlers.clearExecutionState(); break
    case 'set_editor_readonly': {
      const payload = readRecord(message.payload)
      if (!payload || typeof payload.readOnly !== 'boolean') return reject(message.type, 'invalid read-only state')
      handlers.setReadOnly(payload.readOnly)
      break
    }
  }
}

function routeExecutionStatus(
  message: BridgeMessage,
  handlers: EditorBridgeHandlers,
  status: 'running' | 'success' | 'error',
): void {
  const payload = readRecord(message.payload)
  if (!payload || !isNonEmptyString(payload.nodeId)) return reject(message.type, 'invalid node id')
  handlers.updateExecutionStatus(payload.nodeId, status)
}

function reject(type: string, reason: string): void {
  console.warn(`[editor bridge] Ignored "${type}": ${reason}.`)
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
