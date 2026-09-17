import {
  Box,
  Chip,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TableSortLabel,
  Typography,
  Tooltip,
} from '@mui/material'
import { useState, useMemo } from 'react'
import { useCumulativeWaits } from '../../hooks/useWaitStats'
import LoadingSpinner from '../../components/LoadingSpinner'
import ErrorAlert from '../../components/ErrorAlert'
import ProgressBar from '../../components/ProgressBar'
import type { RefreshInterval, WaitStatCumulative } from '../../types/waitStats'

type SortKey = keyof WaitStatCumulative
type SortDir = 'asc' | 'desc'

interface Props { interval: RefreshInterval }

export default function WaitTypes({ interval }: Props) {
  const { data, isLoading, isError, error, refetch } = useCumulativeWaits(interval)
  const [sortKey, setSortKey] = useState<SortKey>('pctOfTotalWaits')
  const [sortDir, setSortDir] = useState<SortDir>('desc')
  const [catFilter, setCatFilter] = useState<string>('All')

  const categories = useMemo(() => {
    if (!data) return []
    return ['All', ...Array.from(new Set(data.map((d) => d.waitCategory)))]
  }, [data])

  const sorted = useMemo(() => {
    if (!data) return []
    let rows = catFilter === 'All' ? data : data.filter((d) => d.waitCategory === catFilter)
    return [...rows].sort((a, b) => {
      const av = a[sortKey], bv = b[sortKey]
      if (typeof av === 'number' && typeof bv === 'number')
        return sortDir === 'asc' ? av - bv : bv - av
      return sortDir === 'asc'
        ? String(av).localeCompare(String(bv))
        : String(bv).localeCompare(String(av))
    })
  }, [data, sortKey, sortDir, catFilter])

  const handleSort = (key: SortKey) => {
    if (sortKey === key) setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'))
    else { setSortKey(key); setSortDir('desc') }
  }

  if (isLoading) return <LoadingSpinner message="Loading wait types..." />
  if (isError) return <ErrorAlert message={(error as Error).message} onRetry={() => refetch()} />

  const catColorMap = Object.fromEntries(data!.map((d) => [d.waitCategory, d.categoryColor]))

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} mb={0.5}>Cumulative Wait Types</Typography>
      <Typography variant="body2" color="text.secondary" mb={2}>
        Aggregated wait statistics since last SQL Server restart — {data!.length} wait types
      </Typography>

      {/* Category filter chips */}
      <Box display="flex" gap={1} flexWrap="wrap" mb={2}>
        {categories.map((cat) => (
          <Chip
            key={cat}
            label={cat}
            size="small"
            onClick={() => setCatFilter(cat)}
            sx={{
              cursor: 'pointer',
              border: `1px solid ${cat === 'All' ? '#58A6FF' : catColorMap[cat] ?? '#30363D'}`,
              backgroundColor:
                catFilter === cat
                  ? `${cat === 'All' ? '#58A6FF' : catColorMap[cat] ?? '#58A6FF'}33`
                  : 'transparent',
              color: cat === 'All' ? '#58A6FF' : catColorMap[cat] ?? '#E6EDF3',
              fontWeight: catFilter === cat ? 700 : 500,
            }}
          />
        ))}
      </Box>

      <TableContainer component={Paper} sx={{ bgcolor: '#161B22' }}>
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              {[
                { key: 'waitType' as SortKey, label: 'Wait Type', width: 220 },
                { key: 'waitCategory' as SortKey, label: 'Category', width: 110 },
                { key: 'waitTimeSec' as SortKey, label: 'Total Wait (s)', width: 120 },
                { key: 'pctOfTotalWaits' as SortKey, label: '% of Total', width: 200 },
                { key: 'avgWaitTimeMs' as SortKey, label: 'Avg Wait (ms)', width: 110 },
                { key: 'waitingTasksCount' as SortKey, label: 'Tasks', width: 80 },
                { key: 'signalWaitPct' as SortKey, label: 'Signal %', width: 100 },
                { key: 'maxWaitTimeMs' as SortKey, label: 'Max Wait (ms)', width: 110 },
              ].map((col) => (
                <TableCell key={col.key} sx={{ width: col.width, minWidth: col.width }}>
                  <TableSortLabel
                    active={sortKey === col.key}
                    direction={sortKey === col.key ? sortDir : 'desc'}
                    onClick={() => handleSort(col.key)}
                    sx={{ color: '#8B949E', '&.Mui-active': { color: '#58A6FF' } }}
                  >
                    {col.label}
                  </TableSortLabel>
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {sorted.map((row) => (
              <TableRow key={row.waitType}>
                <TableCell>
                  <Tooltip title={row.waitType} arrow>
                    <Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: '0.78rem', maxWidth: 200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {row.waitType}
                    </Typography>
                  </Tooltip>
                </TableCell>
                <TableCell>
                  <Chip
                    label={row.waitCategory}
                    size="small"
                    sx={{ backgroundColor: `${row.categoryColor}22`, color: row.categoryColor, border: `1px solid ${row.categoryColor}55`, fontSize: '0.7rem' }}
                  />
                </TableCell>
                <TableCell sx={{ color: '#E6EDF3', fontFamily: 'monospace', fontSize: '0.8rem' }}>
                  {row.waitTimeSec.toLocaleString(undefined, { maximumFractionDigits: 0 })}
                </TableCell>
                <TableCell sx={{ width: 200 }}>
                  <ProgressBar value={row.pctOfTotalWaits} color={row.categoryColor} height={5} showLabel />
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem', color: '#8B949E' }}>
                  {row.avgWaitTimeMs.toFixed(1)} ms
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>
                  {row.waitingTasksCount.toLocaleString()}
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem', color: row.signalWaitPct > 25 ? '#FF6B6B' : row.signalWaitPct > 10 ? '#FFA62B' : '#8B949E' }}>
                  {row.signalWaitPct.toFixed(2)}%
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>
                  {row.maxWaitTimeMs.toLocaleString(undefined, { maximumFractionDigits: 0 })} ms
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </Box>
  )
}
