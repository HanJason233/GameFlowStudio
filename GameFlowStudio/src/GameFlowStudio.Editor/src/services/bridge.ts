import type { BridgeMessage } from '../types/workflow'

declare global {
  interface Window {
    chrome?: { webview?: {
      postMessage: (message: unknown) => void
      addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
      removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void
    } }
  }
}

export function postBridgeMessage<T>(type: string, payload?: T, requestId?: string): void {
  const message: BridgeMessage<T> = { type, payload, requestId }
  window.chrome?.webview?.postMessage(message)
}

export function subscribeBridge(listener: (message: BridgeMessage) => void): () => void {
  const handler = (event: MessageEvent) => listener(typeof event.data === 'string' ? JSON.parse(event.data) : event.data as BridgeMessage)
  window.chrome?.webview?.addEventListener('message', handler)
  return () => window.chrome?.webview?.removeEventListener('message', handler)
}
