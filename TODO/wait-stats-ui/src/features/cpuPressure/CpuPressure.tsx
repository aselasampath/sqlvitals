import { Box, Grid, Paper, Typography } from '@mui/material'
import ReactApexChart from 'react-apexcharts'
import type { ApexOptions } from 'apexcharts'
import { useSignalVsResource, useServerHealthKpi } from '../../hooks/useWaitStats'
import LoadingSpinner from '../../components/LoadingSpinner'
import ErrorAlert from '../../components/ErrorAlert'
import KpiCard from '../../components/KpiCard'
import StatusBadge from '../../components/StatusBadge'
import type { RefreshInterval } from '../../types/waitStats'
import SpeedIcon from '@mui/icons-material/Speed'
import MemoryIcon from '@mui/icons-material/Memory'

interface Props { interval: RefreshInterval }

export default function CpuPressure({ interval }: Props) {
  const sig = useSignalVsResource(interval)
  const health = useServerHealthKpi(interval)

  if (sig.isLoading || health.isLoading) return <LoadingSpinner message="Loading CPU data..." />
  if (sig.isError) return <ErrorAlert message={(sig.error as Error).message} onRetry={() => sig.refetch()} />
  if (health.isError) return <ErrorAlert message={(health.error as Error).message} onRetry={() => health.refetch()} />

  // sig.data is a SINGLE server-aggregate object (not an array)
  const sv = sig.data!
  const kpi = health.data!

  const cpuColor =
    kpi.cpuPressureStatus === 'CRITICAL' ? '#FF6B6B' :
    kpi.cpuPressureStatus === 'WARNING' ? '#FFA62B' : '#26E7A6'

  const pleColor =
    kpi.pleStatus === 'LOW' ? '#FF6B6B' :
    kpi.pleStatus === 'GOOD' ? '#26E7A6' : '#FFA62B'

  // Signal vs Resource pie
  const pieSeries = [
    parseFloat(sv.serverSignalWaitPct.toFixed(1)),
    parseFloat(sv.serverResourceWaitPct.toFixed(1)),
  ]
  const pieOpts: ApexOptions = {
    chart: { type: 'donut', background: 'transparent', toolbar: { show: false } },
    labels: ['Signal Wait (CPU Queue)', 'Resource Wait (I/O, Lock, Memory)'],
    colors: ['#FFA62B', '#58A6FF'],
    legend: { position: 'bottom', labels: { colors: '#8B949E' } },
    dataLabels: { enabled: true, formatter: (v) => `${(v as number).toFixed(1)}%` },
    plotOptions: { pie: { donut: { size: '55%' } } },
    stroke: { width: 0 },
    tooltip: { y: { formatter: (v) => `${v}%` } },
    theme: { mode: 'dark' },
  }

  // Radial for signal wait %
  const radialOpts: ApexOptions = {
    chart: { type: 'radialBar', background: 'transparent' },
    plotOptions: {
      radialBar: {
        startAngle: -135,
        endAngle: 135,
        track: { background: '#21262D', strokeWidth: '67%' },
        dataLabels: {
          name: { fontSize: '14px', color: '#8B949E', offsetY: -10 },
          value: { fontSize: '28px', color: '#E6EDF3', fontWeight: 700, offsetY: 5, formatter: (v) => `${v}%` },
        },
      },
    },
    colors: [sv.serverSignalWaitPct > 25 ? '#FF6B6B' : sv.serverSignalWaitPct > 10 ? '#FFA62B' : '#26E7A6'],
    labels: ['Signal Wait'],
    theme: { mode: 'dark' },
  }

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} mb={0.5}>CPU Pressure Analysis</Typography>
      <Typography variant="body2" color="text.secondary" mb={3}>
        Signal waits indicate CPU queuing — high signal% means CPU is the bottleneck
      </Typography>

      <Grid container spacing={2} mb={3}>
        <Grid item xs={12} sm={3}>
          <KpiCard
            title="Signal Wait %"
            value={`${sv.serverSignalWaitPct.toFixed(1)}%`}
            subtitle={kpi.cpuPressureStatus}
            color={cpuColor}
            icon={<SpeedIcon sx={{ fontSize: 16, color: cpuColor }} />}
            tooltip="Signal waits >10% = CPU pressure, >25% = critical"
          />
        </Grid>
        <Grid item xs={12} sm={3}>
          <KpiCard
            title="Resource Wait %"
            value={`${sv.serverResourceWaitPct.toFixed(1)}%`}
            subtitle="I/O, Lock, Memory waits"
            color="#58A6FF"
          />
        </Grid>
        <Grid item xs={12} sm={3}>
          <KpiCard
            title="Page Life Expectancy"
            value={`${kpi.pageLifeExpectancy.toLocaleString()}s`}
            subtitle={kpi.pleStatus}
            color={pleColor}
            icon={<MemoryIcon sx={{ fontSize: 16, color: pleColor }} />}
          />
        </Grid>
        <Grid item xs={12} sm={3}>
          <KpiCard
            title="Blocked Requests"
            value={kpi.blockedRequests.toLocaleString()}
            subtitle={kpi.blockedRequests > 0 ? 'Blocking detected' : 'No blocking'}
            color={kpi.blockedRequests > 0 ? '#FF6B6B' : '#26E7A6'}
          />
        </Grid>
      </Grid>

      <Box display="flex" gap={1} flexWrap="wrap" mb={3}>
        <StatusBadge label={`CPU: ${kpi.cpuPressureStatus}`} color={cpuColor} />
        <StatusBadge label={`PLE: ${kpi.pleStatus}`} color={pleColor} />
        <StatusBadge label={`CPU Pressure: ${sv.cpuPressureLevel}`} color={cpuColor} />
        <StatusBadge label={`Sessions: ${kpi.activeUserSessions}`} color="#58A6FF" />
        {kpi.blockedRequests > 0 && (
          <StatusBadge label={`Blocked: ${kpi.blockedRequests}`} color="#FF6B6B" />
        )}
      </Box>

      <Grid container spacing={3}>
        <Grid item xs={12} md={3}>
          <Paper sx={{ p: 2, bgcolor: '#161B22', height: '100%' }}>
            <Typography variant="subtitle2" color="text.secondary" mb={1} textAlign="center">Signal Wait %</Typography>
            <ReactApexChart options={radialOpts} series={[parseFloat(sv.serverSignalWaitPct.toFixed(1))]} type="radialBar" height={220} />
          </Paper>
        </Grid>
        <Grid item xs={12} md={5}>
          <Paper sx={{ p: 2, bgcolor: '#161B22', height: '100%' }}>
            <Typography variant="subtitle2" color="text.secondary" mb={1}>Signal vs Resource Split</Typography>
            <ReactApexChart options={pieOpts} series={pieSeries} type="donut" height={260} />
          </Paper>
        </Grid>
        <Grid item xs={12} md={4}>
          <Paper sx={{ p: 2, bgcolor: '#161B22', height: '100%' }}>
            <Typography variant="subtitle2" color="text.secondary" mb={2}>Server Wait Totals</Typography>
            {[
              { label: 'Total Wait', value: sv.totalWaitSec.toLocaleString(undefined, { maximumFractionDigits: 0 }), unit: 's', color: '#E6EDF3' },
              { label: 'Signal Wait', value: sv.totalSignalSec.toLocaleString(undefined, { maximumFractionDigits: 0 }), unit: 's', color: '#FFA62B' },
              { label: 'Resource Wait', value: sv.totalResourceSec.toLocaleString(undefined, { maximumFractionDigits: 0 }), unit: 's', color: '#58A6FF' },
            ].map((item) => (
              <Box key={item.label} display="flex" justifyContent="space-between" alignItems="center" py={1}
                sx={{ borderBottom: '1px solid #21262D' }}>
                <Typography variant="body2" color="text.secondary">{item.label}</Typography>
                <Typography variant="body2" fontFamily="monospace" fontWeight={700} sx={{ color: item.color }}>
                  {item.value} {item.unit}
                </Typography>
              </Box>
            ))}
            <Box mt={2}>
              <Typography variant="caption" color="text.secondary">{sv.cpuPressureDescription}</Typography>
            </Box>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  )
}
