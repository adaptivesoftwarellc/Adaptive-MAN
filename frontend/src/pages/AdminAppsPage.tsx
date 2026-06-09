import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api } from '../lib/api';
import { Badge, EmptyState, Modal, PageHeader, Panel, Skeleton } from '../components/ui';
import { GridIcon, KeyIcon, PlusIcon, WifiOffIcon } from '../components/icons';

export function AdminAppsPage() {
  const { data, isLoading, isError, refetch } = useQuery({ queryKey: ['admin-apps'], queryFn: api.adminApps });
  const [showCreate, setShowCreate] = useState(false);

  return (
    <div className="p-6">
      <PageHeader
        title="Apps"
        description="Onboard apps and inspect environments. Mint and revoke keys from each environment."
        actions={
          <button
            onClick={() => setShowCreate(true)}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm transition hover:bg-brand-500"
          >
            <PlusIcon className="h-4 w-4" /> New app
          </button>
        }
      />

      {isError && (
        <Panel>
          <EmptyState tone="error" icon={<WifiOffIcon className="h-5 w-5" />} title="Failed to load apps" onRetry={() => refetch()} />
        </Panel>
      )}

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {isLoading &&
          Array.from({ length: 3 }).map((_, i) => (
            <Panel key={i} className="p-4">
              <Skeleton className="h-4 w-28" />
              <Skeleton className="mt-2 h-3 w-20" />
              <Skeleton className="mt-4 h-3 w-full" />
            </Panel>
          ))}

        {data?.map((a) => (
          <Panel key={a.id} className="p-4">
            <div className="flex items-start gap-3">
              <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand-50 text-brand-600">
                <GridIcon className="h-4 w-4" />
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline justify-between gap-2">
                  <div className="truncate text-sm font-semibold text-slate-900">{a.name}</div>
                  {!a.is_active && <Badge color="gray">inactive</Badge>}
                </div>
                <div className="font-mono text-xs text-slate-500">{a.slug}</div>
              </div>
            </div>
            {a.description && <p className="mt-3 text-sm text-slate-600">{a.description}</p>}

            <div className="mt-3 divide-y divide-slate-100 border-t border-slate-100">
              {a.environments.map((e) => (
                <div key={e.id} className="flex items-center justify-between gap-2 py-2">
                  <div className="flex items-center gap-2">
                    <span className="text-sm text-slate-700">{e.name}</span>
                    <Badge color={e.active_key_count > 0 ? 'green' : 'gray'}>
                      {e.active_key_count} active
                    </Badge>
                    {e.total_key_count > e.active_key_count && (
                      <span className="text-xs text-slate-400">{e.total_key_count} total</span>
                    )}
                  </div>
                  <Link
                    to={`/admin/keys/${encodeURIComponent(a.slug)}/${encodeURIComponent(e.name)}`}
                    className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs font-medium text-brand-600 transition hover:bg-brand-50"
                  >
                    <KeyIcon className="h-3.5 w-3.5" /> Keys
                  </Link>
                </div>
              ))}
              {a.environments.length === 0 && (
                <span className="block py-2 text-xs text-slate-400">no environments</span>
              )}
            </div>
          </Panel>
        ))}
      </div>

      {data?.length === 0 && (
        <Panel>
          <EmptyState icon={<GridIcon className="h-5 w-5" />} title="No apps registered" description="Create your first app to start ingesting telemetry." />
        </Panel>
      )}

      {showCreate && <CreateAppModal onClose={() => setShowCreate(false)} />}
    </div>
  );
}

function CreateAppModal({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [description, setDescription] = useState('');
  const [environments, setEnvironments] = useState('Development, UAT, Production');

  const mutation = useMutation({
    mutationFn: (body: { name: string; slug: string; description?: string; environments?: string[] }) =>
      api.createApp(body),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin-apps'] });
      onClose();
    },
  });

  const submit = () => {
    const envs = environments
      .split(',')
      .map((e) => e.trim())
      .filter(Boolean);
    mutation.mutate({
      name: name.trim(),
      slug: slug.trim().toLowerCase(),
      description: description.trim() || undefined,
      environments: envs.length ? envs : undefined,
    });
  };

  const canSubmit = name.trim().length > 0 && slug.trim().length > 0 && !mutation.isPending;

  return (
    <Modal header={<h2 className="text-base font-semibold text-slate-900">New app</h2>} onClose={onClose}>
      <div className="space-y-4 p-6">
        <Field label="Name">
          <input className={inputCls} value={name} onChange={(e) => setName(e.target.value)} placeholder="SCH UI" autoFocus />
        </Field>
        <Field label="Slug" hint="Lowercase identifier used in API paths.">
          <input className={`${inputCls} font-mono`} value={slug} onChange={(e) => setSlug(e.target.value)} placeholder="sch-ui" />
        </Field>
        <Field label="Description" hint="Optional.">
          <input className={inputCls} value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Patient-facing scheduling web app" />
        </Field>
        <Field label="Environments" hint="Comma-separated. Reuses existing on a duplicate slug.">
          <input className={inputCls} value={environments} onChange={(e) => setEnvironments(e.target.value)} />
        </Field>

        {mutation.isError && (
          <p className="text-sm text-rose-600">Could not create the app. Check the slug and try again.</p>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <button onClick={onClose} className="rounded-lg px-3 py-1.5 text-sm font-medium text-slate-600 transition hover:bg-slate-100">
            Cancel
          </button>
          <button
            onClick={submit}
            disabled={!canSubmit}
            className="rounded-lg bg-brand-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm transition hover:bg-brand-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {mutation.isPending ? 'Creating…' : 'Create app'}
          </button>
        </div>
      </div>
    </Modal>
  );
}

const inputCls =
  'w-full rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-900 shadow-sm outline-none transition focus:border-brand-400 focus:ring-2 focus:ring-brand-100';

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-slate-700">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs text-slate-400">{hint}</span>}
    </label>
  );
}
