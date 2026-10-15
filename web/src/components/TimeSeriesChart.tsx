import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { formatTime } from '../format';

export type Series<T> = { key: string; label: string; color: string; value: (point: T) => number };

type Props<T extends { at: number }> = {
    points: T[];
    series: Series<T>[];
    formatValue: (value: number) => string;
    /** Fixed upper bound (e.g. 100 for percentages); otherwise derived from the data. */
    yMax?: number;
    height?: number;
    /** Accessible summary of what the chart shows. */
    label: string;
};

const margin = { top: 8, bottom: 22, left: 56 };

/**
 * Line chart over time: one y-axis, recessive grid, 2px lines, area fill only for a single
 * series, crosshair + tooltip on hover, legend with direct end labels when there are two series.
 */
export function TimeSeriesChart<T extends { at: number }>({ points, series, formatValue, yMax, height = 180, label }: Props<T>) {
    const containerRef = useRef<HTMLDivElement>(null);
    const [width, setWidth] = useState(600);
    const [hover, setHover] = useState<number | null>(null);

    useLayoutEffect(() => {
        const element = containerRef.current;

        if (!element) return;

        const observer = new ResizeObserver(([entry]) => setWidth(Math.max(240, entry.contentRect.width)));
        observer.observe(element);

        return () => observer.disconnect();
    }, []);

    const multi = series.length > 1;
    // Room for direct end labels when lines need telling apart.
    const right = multi ? 36 : 12;
    const plotWidth = width - margin.left - right;
    const plotHeight = height - margin.top - margin.bottom;

    const geometry = useMemo(() => {
        if (points.length === 0) return null;

        const minX = points[0].at;
        const maxX = points[points.length - 1].at;
        const dataMax = Math.max(...points.flatMap((p) => series.map((s) => s.value(p))));
        const top = yMax ?? niceCeiling(dataMax);
        const x = (at: number) => margin.left + (maxX === minX ? plotWidth : ((at - minX) / (maxX - minX)) * plotWidth);
        const y = (value: number) => margin.top + plotHeight - (Math.min(value, top) / top) * plotHeight;
        const yTicks = [0, top / 2, top];
        const xTicks = Array.from({ length: Math.min(5, points.length) }, (_, i) =>
            points[Math.round((i * (points.length - 1)) / Math.max(1, Math.min(5, points.length) - 1))].at,
        );

        return { x, y, yTicks, xTicks };
    }, [points, series, yMax, plotWidth, plotHeight]);

    if (!geometry) {
        return (
            <div ref={containerRef} className="flex items-center justify-center text-sm text-muted" style={{ height }}>
                No data yet — samples are collected every 10 seconds.
            </div>
        );
    }

    const { x, y, yTicks, xTicks } = geometry;
    const hovered = hover === null ? null : points[hover];
    const last = points[points.length - 1];

    function onMove(event: React.PointerEvent<SVGRectElement>) {
        const rect = event.currentTarget.getBoundingClientRect();
        const ratio = (event.clientX - rect.left) / rect.width;
        setHover(Math.min(points.length - 1, Math.max(0, Math.round(ratio * (points.length - 1)))));
    }

    return (
        <div ref={containerRef} className="relative">
            {multi && (
                <ul className="mb-2 flex flex-wrap gap-4 text-xs text-muted">
                    {series.map((s) => (
                        <li key={s.key} className="flex items-center gap-1.5">
                            <span className="h-0.5 w-4 rounded-full" style={{ background: s.color }} />
                            {s.label}
                        </li>
                    ))}
                </ul>
            )}
            <svg width={width} height={height} role="img" aria-label={label} className="block overflow-visible">
                {yTicks.map((tick) => (
                    <g key={tick}>
                        <line x1={margin.left} x2={width - right} y1={y(tick)} y2={y(tick)} stroke="var(--chart-grid)" strokeWidth={1} />
                        <text x={margin.left - 8} y={y(tick)} dy="0.32em" textAnchor="end" className="fill-slate-500 text-[11px] tabular-nums">
                            {formatValue(tick)}
                        </text>
                    </g>
                ))}
                {xTicks.map((tick, i) => (
                    <text
                        key={`${tick}-${i}`}
                        x={x(tick)}
                        y={height - 6}
                        textAnchor={i === 0 ? 'start' : i === xTicks.length - 1 ? 'end' : 'middle'}
                        className="fill-slate-500 text-[11px] tabular-nums"
                    >
                        {formatTime(tick).slice(0, 5)}
                    </text>
                ))}
                {series.map((s) => {
                    const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.at).toFixed(1)},${y(s.value(p)).toFixed(1)}`).join('');

                    return (
                        <g key={s.key}>
                            {!multi && (
                                <path
                                    d={`${line}L${x(points[points.length - 1].at)},${y(0)}L${x(points[0].at)},${y(0)}Z`}
                                    fill={s.color}
                                    fillOpacity={0.12}
                                />
                            )}
                            <path d={line} fill="none" stroke={s.color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
                            {multi && (
                                <text x={x(last.at) + 6} y={y(s.value(last))} dy="0.32em" className="fill-slate-600 text-[11px] dark:fill-slate-400">
                                    {s.label.split(" ")[0]}
                                </text>
                            )}
                        </g>
                    );
                })}
                {hovered && (
                    <g pointerEvents="none">
                        <line x1={x(hovered.at)} x2={x(hovered.at)} y1={margin.top} y2={margin.top + plotHeight} stroke="var(--chart-axis)" strokeWidth={1} />
                        {series.map((s) => (
                            <circle key={s.key} cx={x(hovered.at)} cy={y(s.value(hovered))} r={4} fill={s.color} stroke="var(--chart-surface)" strokeWidth={2} />
                        ))}
                    </g>
                )}
                <rect
                    x={margin.left}
                    y={margin.top}
                    width={plotWidth}
                    height={plotHeight}
                    fill="transparent"
                    onPointerMove={onMove}
                    onPointerLeave={() => setHover(null)}
                />
            </svg>
            {hovered && (
                <div
                    className="pointer-events-none absolute top-0 z-10 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs shadow-md dark:border-slate-700 dark:bg-slate-900"
                    style={x(hovered.at) > width / 2 ? { right: width - x(hovered.at) + 12 } : { left: x(hovered.at) + 12 }}
                >
                    <p className="mb-1 text-muted tabular-nums">{formatTime(hovered.at)}</p>
                    {series.map((s) => (
                        <p key={s.key} className="flex items-center gap-2 whitespace-nowrap">
                            <span className="size-2 rounded-full" style={{ background: s.color }} />
                            <span className="text-muted">{s.label}</span>
                            <span className="ml-auto font-medium tabular-nums">{formatValue(s.value(hovered))}</span>
                        </p>
                    ))}
                </div>
            )}
        </div>
    );
}

/** Rounds up to 1, 2 or 5 × 10^n so axis labels stay readable. */
function niceCeiling(value: number) {
    if (value <= 0) return 1;

    const magnitude = 10 ** Math.floor(Math.log10(value));
    const normalized = value / magnitude;
    const step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;

    return step * magnitude;
}
