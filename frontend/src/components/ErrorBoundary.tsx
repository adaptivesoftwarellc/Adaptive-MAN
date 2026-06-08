import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { EmptyState } from './ui';
import { AlertTriangleIcon } from './icons';

/**
 * Top-level render guard. A query failure (backend unreachable, etc.) is handled per-page via
 * React Query's error state; this catches the *other* class of failure — a render-time throw
 * (e.g. malformed data) that would otherwise white-screen the whole dashboard.
 *
 * Keyed by route in App.tsx so navigating away from a crashed page clears the error.
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Logged to the console for now; Phase 8 can forward this to the backend.
    console.error('Dashboard render error:', error, info);
  }

  reset = () => this.setState({ error: null });

  render() {
    if (this.state.error) {
      return (
        <div className="p-6">
          <EmptyState
            tone="error"
            icon={<AlertTriangleIcon className="h-5 w-5" />}
            title="Something went wrong"
            description={this.state.error.message || 'An unexpected error occurred while rendering this page.'}
            onRetry={this.reset}
          />
        </div>
      );
    }
    return this.props.children;
  }
}
