import { Area, AreaChart, ResponsiveContainer } from 'recharts';
import { useId } from 'react';
import type { SparklinePoint } from '../lib/api';

export function Sparkline({
  data,
  stroke = '#6366f1',
}: {
  data: SparklinePoint[] | undefined;
  stroke?: string;
}) {
  const gradientId = useId();

  if (!data || data.length === 0) {
    return <div className="flex h-full items-center text-xs text-slate-300">no data</div>;
  }
  return (
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={data} margin={{ top: 4, right: 0, bottom: 0, left: 0 }}>
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={stroke} stopOpacity={0.25} />
            <stop offset="100%" stopColor={stroke} stopOpacity={0} />
          </linearGradient>
        </defs>
        <Area
          type="monotone"
          dataKey="c"
          stroke={stroke}
          strokeWidth={2}
          fill={`url(#${gradientId})`}
          dot={false}
          isAnimationActive={false}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}
