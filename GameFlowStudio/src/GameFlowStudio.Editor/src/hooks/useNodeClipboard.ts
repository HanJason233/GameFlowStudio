import { useEffect, useRef } from 'react'
import type { GameFlowNode } from '../types/workflow'

export function useNodeClipboard(
  nodes: GameFlowNode[],
  setNodes: React.Dispatch<React.SetStateAction<GameFlowNode[]>>,
  readOnly: boolean,
  notifyChanged: () => void,
): void {
  const clipboard = useRef<GameFlowNode[]>([])

  useEffect(() => {
    const keyHandler = (event: KeyboardEvent) => {
      if (readOnly) return
      if (event.ctrlKey && event.key.toLowerCase() === 'c') copySelectedNodes(nodes, clipboard)
      if (event.ctrlKey && event.key.toLowerCase() === 'v' && clipboard.current.length) pasteNodes(event, clipboard.current, setNodes, notifyChanged)
    }
    window.addEventListener('keydown', keyHandler)
    return () => window.removeEventListener('keydown', keyHandler)
  }, [nodes, notifyChanged, readOnly, setNodes])
}

function copySelectedNodes(nodes: GameFlowNode[], clipboard: React.MutableRefObject<GameFlowNode[]>): void {
  clipboard.current = nodes.filter(node => node.selected).map(node => structuredClone(node))
}

function pasteNodes(event: KeyboardEvent, clipboard: GameFlowNode[], setNodes: React.Dispatch<React.SetStateAction<GameFlowNode[]>>, notifyChanged: () => void): void {
  event.preventDefault()
  const pasted = clipboard.map(node => ({ ...structuredClone(node), id: `${node.data.nodeType}-${crypto.randomUUID()}`, selected: true, position: { x: node.position.x + 32, y: node.position.y + 32 } }))
  setNodes(current => [...current.map(node => ({ ...node, selected: false })), ...pasted])
  setTimeout(notifyChanged, 0)
}
