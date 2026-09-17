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
  Tooltip,
  Typography,
} from '@mui/material'
import BlockIcon from '@mui/icons-material/Block'
import { useActiveWaits } from '../../hooks/useWaitStats'
import LoadingSpinner from '../../components/LoadingSpinner'
import ErrorAlert from '../../components/ErrorAlert'
import StatusBadge from '../../components/StatusBadge'
import type { RefreshInterval } from '../../types/waitStats'

interface Props { interval: RefreshInterval }

export default function ActiveWaits({ interval }: Props) {
  const { data, isLoading, isError, error, refetch } = useActiveWaits(interval)

  if (isLoading) return <LoadingSpinner message="Loading active waits..." />
  if (isError) return <ErrorAlert message={(error as Error).message} onRetry={() => refetch()} />

  const blocked = data!.filter((r) => r.isBlocked)
  const highWait = data!.filter((r) => r.waitTimeSec > 30)

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} mb={0.5}>Active Waits</Typography>
      <Typography variant="body2" color="text.secondary" mb={2}>
        Live sessions currently waiting — refreshes every {interval}s
      </Typography>

      <Box display="flex" gap={2} mb={2} flexWrap="wrap">
        <StatusBadge label={`${data!.length} Active Waiters`} color="#58A6FF" />
        {blocked.length > 0 && (
          <StatusBadge label={`${blocked.length} Blocked Sessions`} color="#FF6B6B" />
        )}
        {highWait.length > 0 && (
          <StatusBadge label={`${highWait.length} Waiting > 30s`} color="#FFA62B" />
        )}
      </Box>

      {data!.length === 0 ? (
        <Box py={6} textAlign="center">
          <Typography color="text.secondary">No active waits at this moment</Typography>
        </Box>
      ) : (
        <TableContainer component={Paper} sx={{ bgcolor: '#161B22' }}>
          <Table size="small" stickyHeader>
            <TableHead>
              <TableRow>
                <TableCell>SPID</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Wait Type</TableCell>
                <TableCell>Wait (s)</TableCell>
                <TableCell>Category</TableCell>
                <TableCell>Database</TableCell>
                <TableCell>Login</TableCell>
                <TableCell>Host</TableCell>
                <TableCell>Command</TableCell>
                <TableCell>Blocked By</TableCell>
                <TableCell>SQL Text</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {data!.map((row) => (
                <TableRow
                  key={`${row.sessionId}-${row.requestId}`}
                  sx={{
                    bgcolor: row.isBlocked ? '#FF6B6B08' : 'inherit',
                    '&:hover': { bgcolor: row.isBlocked ? '#FF6B6B12' : 'rgba(88,166,255,0.05)' },
                  }}
                >
                  <TableCell sx={{ fontFamily: 'monospace', fontWeight: 700, color: row.isBlocked ? '#FF6B6B' : '#58A6FF' }}>
                    {row.sessionId}
                    {row.isBlocked && <BlockIcon sx={{ fontSize: 12, ml: 0.5, verticalAlign: 'middle', color: '#FF6B6B' }} />}
                  </TableCell>
                  <TableCell>
                    <Chip
                      label={row.status}
                      size="small"
                      sx={{
                        bgcolor: row.status === 'running' ? '#26E7A622' : row.status === 'suspended' ? '#FFA62B22' : '#55555522',
                        color: row.status === 'running' ? '#26E7A6' : row.status === 'suspended' ? '#FFA62B' : '#8B949E',
                        border: 'none',
                        fontSize: '0.7rem',
                      }}
                    />
                  </TableCell>
                  <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.75rem', maxWidth: 160 }}>
                    <Tooltip title={row.waitType} arrow>
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', display: 'block', whiteSpace: 'nowrap' }}>
                        {row.waitType}
                      </span>
                    </Tooltip>
                  </TableCell>
                  <TableCell sx={{ fontFamily: 'monospace', color: row.waitTimeSec > 30 ? '#FF6B6B' : row.waitTimeSec > 10 ? '#FFA62B' : '#E6EDF3' }}>
                    {row.waitTimeSec.toFixed(1)}
                  </TableCell>
                  <TableCell>
                    <Chip
                      label={row.waitCategory}
                      size="small"
                      sx={{ bgcolor: `${row.waitCategoryColor}22`, color: row.waitCategoryColor, fontSize: '0.7rem', border: 'none' }}
                    />
                  </TableCell>
                  <TableCell sx={{ fontSize: '0.78rem' }}>{row.databaseName}</TableCell>
                  <TableCell sx={{ fontSize: '0.78rem' }}>{row.loginName}</TableCell>
                  <TableCell sx={{ fontSize: '0.78rem' }}>{row.hostName}</TableCell>
                  <TableCell sx={{ fontSize: '0.75rem', fontFamily: 'monospace' }}>{row.command}</TableCell>
                  <TableCell sx={{ fontFamily: 'monospace', color: row.blockedBy ? '#FF6B6B' : '#555' }}>
                    {row.blockedBy ?? '—'}
                  </TableCell>
                  <TableCell sx={{ maxWidth: 200 }}>
                    <Tooltip title={row.sqlText ?? 'N/A'} arrow placement="left">
                      <Typography variant="caption" sx={{ fontFamily: 'monospace', overflow: 'hidden', textOverflow: 'ellipsis', display: 'block', whiteSpace: 'nowrap', maxWidth: 180, color: '#8B949E' }}>
                        {row.sqlText ?? '—'}
                      </Typography>
                    </Tooltip>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  )
}
