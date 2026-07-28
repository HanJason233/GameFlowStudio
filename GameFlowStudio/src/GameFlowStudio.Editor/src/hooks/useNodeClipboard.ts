import { useEffect, useRef } from 'react'
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
): void {
  const clipboard = useRef<ClipboardNode[]>([])

  useEffect(() => {
    const keyHandler = (event: KeyboardEvent) => {
      if (readOnly) return
      if (event.ctrlKey && event.key.toLowerCase() === 'c') copySelectedNodes(nodes, clipboard)
      if (event.ctrlKey && event.key.toLowerCase() === 'v' && clipboard.current.length) pasteNodes(event, clipboard.current, setNodes, markChanged)
    }
    window.addEventListener('keydown', keyHandler)
    return () => window.removeEventListener('keydown', keyHandler)
  }, [markChanged, nodes, readOnly, setNodes])
}

function copySelectedNodes(nodes: GameFlowNode[], clipboard: React.MutableRefObject<ClipboardNode[]>): void {
  clipboard.current = nodes.filter(node => node.selected).map(node => ({
    position: { ...node.position },
    data: {
      nodeType: node.data.nodeType,
      displayName: node.data.displayName,
      parameters: structuredClone(node.data.parameters),
    },
  }))
}

function pasteNodes(event: KeyboardEvent, clipboard: ClipboardNode[], setNodes: React.Dispatch<React.SetStateAction<GameFlowNode[]>>, markChanged: () => void): void {
  event.preventDefault()
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
