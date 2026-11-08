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
    case 'request_import_workflow': {
      if (!message.requestId) return reject(message.type, 'missing request id')
      if (!isWorkflowDocument(message.payload)) {
        postBridgeMessage('workflow_imported', { success: false, error: 'invalid workflow document' }, message.requestId)
        return reject(message.type, 'invalid workflow document')
      }
      try {
        handlers.importDocument(message.payload)
        postBridgeMessage('workflow_imported', { success: true }, message.requestId)
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error)
        postBridgeMessage('workflow_imported', { success: false, error: detail }, message.requestId)
        reportError(message.type, error)
      }
      break
    }
    case 'request_workflow_export':
      if (!message.requestId) return reject(message.type, 'missing request id')
      postBridgeMessage('workflow_exported', handlers.exportDocument(), message.requestId); break
    case 'add_node': {
      const payload = readRecord(message.payload)
      if (!payload || !isNodeType(payload.nodeType)) return reject(message.type, 'invalid node type')
      try {
        handlers.addNode(payload.nodeType)
      } catch (error) {
        reportError(message.type, error)
      }
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
    case 'request_set_editor_readonly': {
      if (!message.requestId) return reject(message.type, 'missing request id')
      const payload = readRecord(message.payload)
      if (!payload || typeof payload.readOnly !== 'boolean') {
        postBridgeMessage('editor_readonly_set', { success: false, error: 'invalid read-only state' }, message.requestId)
        return reject(message.type, 'invalid read-only state')
      }
      handlers.setReadOnly(payload.readOnly)
      postBridgeMessage('editor_readonly_set', { success: true, readOnly: payload.readOnly }, message.requestId)
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

function reportError(type: string, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error)
  console.error(`[editor bridge] Failed to handle "${type}": ${message}`)
  postBridgeMessage('editor_error', { type, message })
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}
