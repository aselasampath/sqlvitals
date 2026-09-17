import { Grid, Typography, Box, Divider } from '@mui/material'
import KpiCard from '../../components/KpiCard'
import LoadingSpinner from '../../components/LoadingSpinner'
import ErrorAlert from '../../components/ErrorAlert'
import { useServerHealthKpi, useWaitCategories, useTopWaitTypes } from '../../hooks/useWaitStats'
import type { RefreshInterval } from '../../types/waitStats'
import ReactApexChart from 'react-apexcharts'
import type { ApexOptions } from 'apexcharts'
import StatusBadge from '../../components/StatusBadge'
import MemoryIcon from '@mui/icons-material/Memory'
import SpeedIcon from '@mui/icons-material/Speed'
import StorageIcon from '@mui/icons-material/Storage'
import BarChartIcon from '@mui/icons-material/BarChart'

interface Props { interval: RefreshInterval }

export default function Overview({ interval }: Props) {
  const health = useServerHealthKpi(interval)
  const categories = useWaitCategories(interval)
  const topWaits = useTopWaitTypes(interval)

  if (health.isLoading) return <LoadingSpinner message="Loading server health..." />
  if (health.isError) return <ErrorAlert message={(health.error as Error).message} onRetry={() => health.refetch()} />

  const kpi = health.data!

  const cpuColor =
    kpi.cpuPressureStatus === 'CRITICAL' ? '#FF6B6B' :
    kpi.cpuPressureStatus === 'WARNING' ? '#FFA62B' : '#26E7A6'

  const pleColor =
    kpi.pleStatus === 'LOW' ? '#FF6B6B' :
    kpi.pleStatus === 'GOOD' ? '#26E7A6' : '#FFA62B'

  const memColor = kpi.memoryUtilizationPct > 90 ? '#FF6B6B' : kpi.memoryUtilizationPct > 75 ? '#FFA62B' : '#26E7A6'

  // Category donut
  const catLoaded = !categories.isLoading && categories.data
  const donutSeries = catLoaded ? categories.data!.map((c) => parseFloat(c.pctOfTotalWaits.toFixed(1))) : []
  const donutLabels = catLoaded ? categories.data!.map((c) => c.waitCategory) : []
  const donutColors = catLoaded ? categories.data!.map((c) => c.categoryColor) : []

  const donutOptions: ApexOptions = {
    chart: { type: 'donut', background: 'transparent', toolbar: { show: false } },
    labels: donutLabels,
    colors: donutColors,
    legend: { position: 'bottom', labels: { colors: '#8B949E' } },
    dataLabels: { enabled: false },
    plotOptions: { pie: { donut: { size: '60%' } } },
    stroke: { width: 0 },
    tooltip: { y: { formatter: (v) => `${v}%` } },
    theme: { mode: 'dark' },
  }

  // Top waits bar
  const twLoaded = !topWaits.isLoading && topWaits.data
  const barSeries = twLoaded ? [{ name: 'Wait % of Total', data: topWaits.data!.slice(0, 10).map((w) => parseFloat(w.pctOfTotal.toFixed(1))) }] : []
  const barCategories = twLoaded ? topWaits.data!.slice(0, 10).map((w) => w.waitType) : []
  const barColors = twLoaded ? topWaits.data!.slice(0, 10).map((w) => w.categoryColor) : []

  const barOptions: ApexOptions = {
    chart: { type: 'bar', background: 'transparent', toolbar: { show: false } },
    plotOptions: { bar: { horizontal: true, borderRadius: 4, distributed: true } },
    colors: barColors,
    xaxis: { categories: barCategories, labels: { style: { colors: '#8B949E', fontSize: '11px' } } },
    yaxis: { labels: { style: { colors: '#8B949E', fontSize: '11px' } } },
    dataLabels: { enabled: true, formatter: (v) => `${v}%`, style: { fontSize: '10px' } },
    legend: { show: false },
    tooltip: { y: { formatter: (v) => `${v}%` } },
    grid: { borderColor: '#21262D' },
    theme: { mode: 'dark' },
  }

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} mb={0.5}>Server Health Overview</Typography>
      <Typography variant="body2" color="text.secondary" mb={3}>
        Real-time SQL Server performance indicators
      </Typography>

      {/* KPI Row */}
      <Grid container spacing={2} mb={3}>
        <Grid item xs={6} sm={3}>
          <KpiCard
            title="Signal Wait %"
            value={`${kpi.globalSignalWaitPct.toFixed(1)}%`}
            subtitle={kpi.cpuPressureStatus}
            color={cpuColor}
            icon={<SpeedIcon sx={{ fontSize: 16, color: cpuColor }} />}
            tooltip="Signal wait ratio — >10% = CPU pressure, >25% = critical"
          />
        </Grid>
        <Grid item xs={6} sm={3}>
          <KpiCard
            title="Page Life Expectancy"
            value={`${kpi.pageLifeExpectancy.toLocaleString()}s`}
            subtitle={kpi.pleStatus}
            color={pleColor}
            icon={<MemoryIcon sx={{ fontSize: 16, color: pleColor }} />}
            tooltip="Seconds a page stays in buffer pool (>300 = healthy)"
          />
        </Grid>
        <Grid item xs={6} sm={3}>
          <KpiCard
            title="Memory Used"
            value={`${kpi.memoryUtilizationPct.toFixed(1)}%`}
            subtitle={`${Math.round(kpi.committedMemMB / 1024)} GB / ${Math.round(kpi.physicalMemoryMB / 1024)} GB`}
            color={memColor}
            icon={<StorageIcon sx={{ fontSize: 16, color: memColor }} />}
            tooltip="Committed vs physical memory"
          />
        </Grid>
        <Grid item xs={6} sm={3}>
          <KpiCard
            title="Batch Requests/s"
            value={kpi.batchRequestsPerSec.toLocaleString()}
            subtitle={`Active sessions: ${kpi.activeUserSessions}`}
            color="#58A6FF"
            icon={<BarChartIcon sx={{ fontSize: 16, color: '#58A6FF' }} />}
            tooltip="Batch requests per second (workload indicator)"
          />
        </Grid>
      </Grid>

      {/* Status Badges */}
      <Box display="flex" gap={1} mb={3} flexWrap="wrap">
        <StatusBadge label={`CPU: ${kpi.cpuPressureStatus}`} color={cpuColor} />
        <StatusBadge label={`PLE: ${kpi.pleStatus}`} color={pleColor} />
        <StatusBadge label={`Memory: ${kpi.memoryUtilizationPct.toFixed(0)}%`} color={memColor} />
        <StatusBadge label={`Sessions: ${kpi.activeUserSessions}`} color="#58A6FF" />
        <StatusBadge label={`Buffer Hit: ${kpi.bufferCacheHitRatio.toFixed(1)}%`} color="#26E7A6" />
        <StatusBadge label={`Blocked: ${kpi.blockedRequests}`} color={kpi.blockedRequests > 0 ? '#FF6B6B' : '#26E7A6'} />
      </Box>

      <Divider sx={{ mb: 3, borderColor: '#30363D' }} />

      {/* Charts Row */}
      <Grid container spacing={3}>
        <Grid item xs={12} md={5}>
          <Typography variant="subtitle1" fontWeight={600} mb={1}>Wait Category Distribution</Typography>
          {catLoaded ? (
            <ReactApexChart options={donutOptions} series={donutSeries} type="donut" height={300} />
          ) : (
            <LoadingSpinner message="Loading categories..." size={32} />
          )}
        </Grid>
        <Grid item xs={12} md={7}>
          <Typography variant="subtitle1" fontWeight={600} mb={1}>Top 10 Wait Types</Typography>
          {twLoaded ? (
            <ReactApexChart options={barOptions} series={barSeries} type="bar" height={300} />
          ) : (
            <LoadingSpinner message="Loading top waits..." size={32} />
          )}
        </Grid>
      </Grid>
    </Box>
  )
}
