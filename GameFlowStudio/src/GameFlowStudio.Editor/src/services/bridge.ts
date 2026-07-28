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
  if (type.trim().length === 0) {
    console.warn('[editor bridge] Ignored a message with an empty type.')
    return
  }

  const message: BridgeMessage<T> = { type, payload, requestId }
  try {
    window.chrome?.webview?.postMessage(message)
  } catch (error) {
    console.error(`[editor bridge] Failed to send "${type}".`, error)
  }
}

export function subscribeBridge(listener: (message: BridgeMessage) => void): () => void {
  const webView = window.chrome?.webview
  if (!webView) return () => undefined

  const handler = (event: MessageEvent) => {
    const message = parseBridgeMessage(event.data)
    if (!message) {
      console.warn('[editor bridge] Ignored a malformed host message.')
      return
    }

    try {
      listener(message)
    } catch (error) {
      console.error(`[editor bridge] Failed to handle "${message.type}".`, error)
    }
  }

  webView.addEventListener('message', handler)
  return () => webView.removeEventListener('message', handler)
}

function parseBridgeMessage(value: unknown): BridgeMessage | null {
  let parsed: unknown = value
  if (typeof value === 'string') {
    try {
      parsed = JSON.parse(value) as unknown
    } catch {
      return null
    }
  }

  if (!isRecord(parsed) || typeof parsed.type !== 'string' || parsed.type.trim().length === 0) return null
  if ('requestId' in parsed && parsed.requestId !== undefined && typeof parsed.requestId !== 'string') return null

  return {
    type: parsed.type,
    requestId: typeof parsed.requestId === 'string' ? parsed.requestId : undefined,
    payload: parsed.payload,
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
