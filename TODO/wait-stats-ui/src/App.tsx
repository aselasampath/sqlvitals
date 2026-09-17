import { useState } from 'react'
import { ThemeProvider, CssBaseline } from '@mui/material'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Box, Container, Tab, Tabs } from '@mui/material'
import darkTheme from './theme/darkTheme'
import TopBar from './components/TopBar'
import Overview from './features/overview/Overview'
import WaitTypes from './features/waitTypes/WaitTypes'
import ActiveWaits from './features/activeWaits/ActiveWaits'
import CpuPressure from './features/cpuPressure/CpuPressure'
import Recommendations from './features/recommendations/Recommendations'
import type { RefreshInterval } from './types/waitStats'

import DashboardIcon from '@mui/icons-material/Dashboard'
import AccessTimeIcon from '@mui/icons-material/AccessTime'
import HourglassEmptyIcon from '@mui/icons-material/HourglassEmpty'
import SpeedIcon from '@mui/icons-material/Speed'
import LightbulbIcon from '@mui/icons-material/Lightbulb'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 2, staleTime: 20_000 },
  },
})

const TABS = [
  { label: 'Overview',        icon: <DashboardIcon fontSize="small" /> },
  { label: 'Wait Types',      icon: <AccessTimeIcon fontSize="small" /> },
  { label: 'Active Waits',    icon: <HourglassEmptyIcon fontSize="small" /> },
  { label: 'CPU Pressure',    icon: <SpeedIcon fontSize="small" /> },
  { label: 'Recommendations', icon: <LightbulbIcon fontSize="small" /> },
]

function Dashboard() {
  const [tab, setTab] = useState(0)
  const [interval, setInterval] = useState<RefreshInterval>(30)

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <TopBar refreshInterval={interval} onIntervalChange={setInterval} />

      <Box sx={{ bgcolor: '#161B22', borderBottom: '1px solid #30363D' }}>
        <Container maxWidth="xl">
          <Tabs
            value={tab}
            onChange={(_, v) => setTab(v)}
            sx={{
              '& .MuiTab-root': { color: '#8B949E', minHeight: 48 },
              '& .Mui-selected': { color: '#58A6FF' },
              '& .MuiTabs-indicator': { backgroundColor: '#58A6FF', height: 2 },
            }}
          >
            {TABS.map((t) => (
              <Tab
                key={t.label}
                label={t.label}
                icon={t.icon}
                iconPosition="start"
                sx={{ textTransform: 'none', fontWeight: 600, fontSize: '0.875rem' }}
              />
            ))}
          </Tabs>
        </Container>
      </Box>

      <Container maxWidth="xl" sx={{ py: 3 }}>
        {tab === 0 && <Overview interval={interval} />}
        {tab === 1 && <WaitTypes interval={interval} />}
        {tab === 2 && <ActiveWaits interval={interval} />}
        {tab === 3 && <CpuPressure interval={interval} />}
        {tab === 4 && <Recommendations interval={interval} />}
      </Container>
    </Box>
  )
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={darkTheme}>
        <CssBaseline />
        <Dashboard />
      </ThemeProvider>
    </QueryClientProvider>
  )
}
