import { Component, type ErrorInfo, type ReactNode } from 'react'

interface EditorErrorBoundaryProps {
  children: ReactNode
}

interface EditorErrorBoundaryState {
  failed: boolean
}

export class EditorErrorBoundary extends Component<EditorErrorBoundaryProps, EditorErrorBoundaryState> {
  public state: EditorErrorBoundaryState = { failed: false }

  public static getDerivedStateFromError(): EditorErrorBoundaryState {
    return { failed: true }
  }

  public componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('[editor] Rendering failed.', error, info)
  }

  public render(): ReactNode {
    if (!this.state.failed) return this.props.children

    return <main className="editor-fallback" role="alert">
      <strong>编辑器暂时无法显示</strong>
      <span>当前画布遇到了异常。重新加载可以恢复编辑器，但尚未保存的修改可能丢失。</span>
      <button type="button" onClick={() => window.location.reload()}>重新加载</button>
    </main>
  }
}
