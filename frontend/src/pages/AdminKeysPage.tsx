import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { api, type ApiKeyDto, type ApiKeyTypeName, type MintKeyResponse } from '../lib/api';
import { Badge, EmptyState, PageHeader, Panel, Skeleton } from '../components/ui';
import { ChevronLeftIcon, KeyIcon, WifiOffIcon } from '../components/icons';

function fmt(iso: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

/** Keys are identified server-side by GUID; we only ever show a masked form. */
function maskId(id: string): string {
  return `${id.slice(0, 8)}…${id.slice(-4)}`;
}

export function AdminKeysPage() {
  const { slug = '', env = '' } = useParams();
  const queryClient = useQueryClient();
  const keysQuery = useQuery({ queryKey: ['admin-keys', slug, env], queryFn: () => api.listKeys(slug, env) });
  const [minted, setMinted] = useState<MintKeyResponse | null>(null);
  const [keyType, setKeyType] = useState<ApiKeyTypeName>('ServerApi');

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['admin-keys', slug, env] });

  const mintMutation = useMutation({
    mutationFn: () => api.mintKey(slug, env, keyType),
    onSuccess: async (res) => {
      setMinted(res);
      await invalidate();
    },
  });

  const revokeMutation = useMutation({
    mutationFn: (id: string) => api.revokeKey(slug, env, id),
    onSuccess: invalidate,
  });

  return (
    <div className="p-6">
      <Link to="/admin/apps" className="mb-3 inline-flex items-center gap-1 text-sm text-slate-500 transition hover:text-slate-700">
        <ChevronLeftIcon className="h-4 w-4" /> Apps
      </Link>

      <PageHeader title={`Keys · ${slug}`} description={`Environment: ${env}`} />

      {/* Mint */}
      <Panel className="mb-4 p-4">
        <div className="flex flex-wrap items-end gap-3">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-slate-700">Key type</span>
            <select
              value={keyType}
              onChange={(e) => setKeyType(e.target.value as ApiKeyTypeName)}
              className="rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-900 shadow-sm outline-none focus:border-brand-400 focus:ring-2 focus:ring-brand-100"
            >
              <option value="ServerApi">Server API (aoserv_)</option>
              <option value="PublicClient">Public client (aopub_)</option>
            </select>
          </label>
          <button
            onClick={() => mintMutation.mutate()}
            disabled={mintMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand-600 px-3 py-2 text-sm font-medium text-white shadow-sm transition hover:bg-brand-500 disabled:opacity-50"
          >
            <KeyIcon className="h-4 w-4" /> {mintMutation.isPending ? 'Minting…' : 'Mint key'}
          </button>
        </div>

        {minted && <PlaintextReveal minted={minted} onDismiss={() => setMinted(null)} />}
      </Panel>

      {keysQuery.isError && (
        <Panel>
          <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load keys" onRetry={() => keysQuery.refetch()} />
        </Panel>
      )}

      <Panel className="overflow-hidden">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wide text-slate-500">
            <tr>
              <th className="px-4 py-2.5 font-medium">Key</th>
              <th className="px-4 py-2.5 font-medium">Type</th>
              <th className="px-4 py-2.5 font-medium">Created</th>
              <th className="px-4 py-2.5 font-medium">Last used</th>
              <th className="px-4 py-2.5 font-medium">Status</th>
              <th className="px-4 py-2.5" />
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {keysQuery.isLoading &&
              Array.from({ length: 3 }).map((_, i) => (
                <tr key={i}>
                  <td className="px-4 py-3" colSpan={6}>
                    <Skeleton className="h-4 w-full" />
                  </td>
                </tr>
              ))}

            {keysQuery.data?.map((k: ApiKeyDto) => (
              <tr key={k.id} className="hover:bg-slate-50/60">
                <td className="px-4 py-3 font-mono text-xs text-slate-600" title={k.id}>{maskId(k.id)}</td>
                <td className="px-4 py-3">
                  <Badge color={k.key_type === 'ServerApi' ? 'indigo' : 'blue'}>{k.key_type}</Badge>
                </td>
                <td className="px-4 py-3 text-slate-600">{fmt(k.created_at)}</td>
                <td className="px-4 py-3 text-slate-600">{fmt(k.last_used_at)}</td>
                <td className="px-4 py-3">
                  {k.is_active ? <Badge color="green">active</Badge> : <Badge color="red">revoked</Badge>}
                </td>
                <td className="px-4 py-3 text-right">
                  {k.is_active && (
                    <button
                      onClick={() => {
                        if (window.confirm('Revoke this key? Any client using it will immediately get 401s.')) {
                          revokeMutation.mutate(k.id);
                        }
                      }}
                      disabled={revokeMutation.isPending}
                      className="rounded-md px-2 py-1 text-xs font-medium text-rose-600 transition hover:bg-rose-50 disabled:opacity-50"
                    >
                      Revoke
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {keysQuery.data?.length === 0 && (
          <EmptyState icon={<KeyIcon className="h-5 w-5" />} title="No keys yet" description="Mint a key above to start authenticating ingest." />
        )}
      </Panel>
    </div>
  );
}

function PlaintextReveal({ minted, onDismiss }: { minted: MintKeyResponse; onDismiss: () => void }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(minted.plaintext_key);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard blocked — user can select manually */
    }
  };

  return (
    <div className="mt-4 rounded-lg border border-amber-300 bg-amber-50 p-4">
      <div className="flex items-center justify-between">
        <p className="text-sm font-semibold text-amber-800">Copy this key now — it is shown only once.</p>
        <button onClick={onDismiss} className="text-xs font-medium text-amber-700 hover:text-amber-900">Dismiss</button>
      </div>
      <p className="mt-1 text-xs text-amber-700">The plaintext is not stored and cannot be retrieved later.</p>
      <div className="mt-3 flex items-center gap-2">
        <code className="flex-1 overflow-x-auto rounded-md border border-amber-200 bg-white px-3 py-2 font-mono text-xs text-slate-800">
          {minted.plaintext_key}
        </code>
        <button
          onClick={copy}
          className="shrink-0 rounded-md bg-amber-600 px-3 py-2 text-xs font-medium text-white transition hover:bg-amber-500"
        >
          {copied ? 'Copied!' : 'Copy'}
        </button>
      </div>
    </div>
  );
}
