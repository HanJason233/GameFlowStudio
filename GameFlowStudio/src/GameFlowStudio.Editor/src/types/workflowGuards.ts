import { nodeTypes, type NodeDataUpdate, type NodeType, type WorkflowDocument } from './workflow'

const nodeTypeSet = new Set<NodeType>(nodeTypes)

export function isNodeType(value: unknown): value is NodeType {
  return typeof value === 'string' && nodeTypeSet.has(value as NodeType)
}

export function isWorkflowDocument(value: unknown): value is WorkflowDocument {
  if (!isRecord(value)
    || value.schemaVersion !== '1.6'
    || !isProjectMetadata(value.project)
    || !isVariables(value.variables)
    || !Array.isArray(value.nodes)
    || !Array.isArray(value.edges)) return false

  const nodeIds = new Set<string>()
  for (const node of value.nodes) {
    if (!isWorkflowNode(node) || nodeIds.has(node.id)) return false
    nodeIds.add(node.id)
  }

  const edgeIds = new Set<string>()
  for (const edge of value.edges) {
    if (!isWorkflowEdge(edge)
      || edgeIds.has(edge.id)
      || !nodeIds.has(edge.source)
      || !nodeIds.has(edge.target)) return false
    edgeIds.add(edge.id)
  }

  return true
}

export function isNodeDataUpdate(value: unknown): value is NodeDataUpdate {
  if (!isRecord(value)) return false
  if ('displayName' in value && typeof value.displayName !== 'string') return false
  if ('parameters' in value && !isRecord(value.parameters)) return false
  return Object.keys(value).every(key => key === 'displayName' || key === 'parameters')
}

export function readRecord(value: unknown): Record<string, unknown> | null {
  return isRecord(value) ? value : null
}

function isProjectMetadata(value: unknown): boolean {
  return isRecord(value)
    && isNonEmptyString(value.id)
    && typeof value.name === 'string'
    && isNonEmptyString(value.createdAt)
    && isNonEmptyString(value.updatedAt)
}

function isVariables(value: unknown): boolean {
  if (!isRecord(value)) return false
  return Object.values(value).every(variable =>
    isRecord(variable)
    && (variable.type === 'string' || variable.type === 'number' || variable.type === 'boolean')
    && Object.hasOwn(variable, 'value'))
}

function isWorkflowNode(value: unknown): value is WorkflowDocument['nodes'][number] {
  return isRecord(value)
    && isNonEmptyString(value.id)
    && isNodeType(value.type)
    && isRecord(value.position)
    && isFiniteNumber(value.position.x)
    && isFiniteNumber(value.position.y)
    && isRecord(value.data)
    && typeof value.data.displayName === 'string'
    && isRecord(value.data.parameters)
}

function isWorkflowEdge(value: unknown): value is WorkflowDocument['edges'][number] {
  return isRecord(value)
    && isNonEmptyString(value.id)
    && isNonEmptyString(value.source)
    && isNonEmptyString(value.sourceHandle)
    && isNonEmptyString(value.target)
    && isNonEmptyString(value.targetHandle)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}
