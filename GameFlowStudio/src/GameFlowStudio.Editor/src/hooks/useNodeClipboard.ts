import { useCallback, useEffect, useRef, useState } from 'react'
import { nextActionId, type GameFlowNode } from '../types/workflow'

interface ClipboardNode {
  position: GameFlowNode['position']
  data: Pick<GameFlowNode['data'], 'nodeType' | 'displayName' | 'parameters'>
}

export function useNodeClipboard(
  nodes: GameFlowNode[],
  setNodes: React.Dispatch<React.SetStateAction<GameFlowNode[]>>,
  readOnly: boolean,
  markChanged: () => void,
): { copyNodes: (nodeIds: string[]) => void; pasteNodes: () => void; hasCopiedNodes: boolean } {
  const clipboard = useRef<ClipboardNode[]>([])
  const [hasCopiedNodes, setHasCopiedNodes] = useState(false)

  const copyNodes = useCallback((nodeIds: string[]) => {
    if (readOnly) return
    const ids = new Set(nodeIds)
    setHasCopiedNodes(copyNodesToClipboard(nodes.filter(node => ids.has(node.id)), clipboard))
  }, [nodes, readOnly])

  const pasteNodes = useCallback(() => {
    if (readOnly || clipboard.current.length === 0) return
    pasteNodesFromClipboard(clipboard.current, setNodes, markChanged)
  }, [markChanged, readOnly, setNodes])

  useEffect(() => {
    const keyHandler = (event: KeyboardEvent) => {
      if (readOnly) return
      if (event.ctrlKey && event.key.toLowerCase() === 'c') setHasCopiedNodes(copyNodesToClipboard(nodes.filter(node => node.selected), clipboard))
      if (event.ctrlKey && event.key.toLowerCase() === 'v' && clipboard.current.length) {
        event.preventDefault()
        pasteNodesFromClipboard(clipboard.current, setNodes, markChanged)
      }
    }
    window.addEventListener('keydown', keyHandler)
    return () => window.removeEventListener('keydown', keyHandler)
  }, [markChanged, nodes, readOnly, setNodes])

  return { copyNodes, pasteNodes, hasCopiedNodes }
}

function copyNodesToClipboard(nodes: GameFlowNode[], clipboard: React.MutableRefObject<ClipboardNode[]>): boolean {
  clipboard.current = nodes.map(node => ({
    position: { ...node.position },
    data: {
      nodeType: node.data.nodeType,
      displayName: node.data.displayName,
      parameters: structuredClone(node.data.parameters),
    },
  }))
  return clipboard.current.length > 0
}

function pasteNodesFromClipboard(clipboard: ClipboardNode[], setNodes: React.Dispatch<React.SetStateAction<GameFlowNode[]>>, markChanged: () => void): void {
  setNodes(current => {
    let nextId = Number(nextActionId(current))
    const pasted = clipboard.map((node): GameFlowNode => ({
      id: String(nextId++),
      type: 'gameNode',
      selected: true,
      position: { x: node.position.x + 32, y: node.position.y + 32 },
      data: {
        nodeType: node.data.nodeType,
        displayName: node.data.displayName,
        parameters: structuredClone(node.data.parameters),
      },
    }))
    return [...current.map(node => ({ ...node, selected: false })), ...pasted]
  })
  markChanged()
}
