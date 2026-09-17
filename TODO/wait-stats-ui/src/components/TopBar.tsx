import {
  AppBar,
  IconButton,
  MenuItem,
  Select,
  SelectChangeEvent,
  Toolbar,
  Tooltip,
  Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import StorageIcon from '@mui/icons-material/Storage'
import type { RefreshInterval } from '../types/waitStats'
import { useQueryClient } from '@tanstack/react-query'

interface TopBarProps {
  refreshInterval: RefreshInterval
  onIntervalChange: (v: RefreshInterval) => void
}

const INTERVALS: { label: string; value: RefreshInterval }[] = [
  { label: '10 s', value: 10 },
  { label: '30 s', value: 30 },
  { label: '1 min', value: 60 },
  { label: '5 min', value: 300 },
  { label: 'Manual', value: 0 },
]

export default function TopBar({ refreshInterval, onIntervalChange }: TopBarProps) {
  const qc = useQueryClient()

  const handleRefreshAll = () => {
    qc.invalidateQueries()
  }

  return (
    <AppBar
      position="sticky"
      elevation={0}
      sx={{ bgcolor: '#161B22', borderBottom: '1px solid #30363D', zIndex: 1200 }}
    >
      <Toolbar sx={{ gap: 1 }}>
        <StorageIcon sx={{ color: '#58A6FF', mr: 1 }} />
        <Typography variant="h6" sx={{ flexGrow: 1, color: '#E6EDF3', fontWeight: 700 }}>
          SQL Wait Stats
        </Typography>

        <Typography variant="caption" color="text.secondary" sx={{ mr: 1 }}>
          Auto-refresh:
        </Typography>
        <Select
          value={refreshInterval}
          size="small"
          onChange={(e: SelectChangeEvent<number>) =>
            onIntervalChange(Number(e.target.value) as RefreshInterval)
          }
          sx={{
            fontSize: '0.8rem',
            color: '#E6EDF3',
            '.MuiOutlinedInput-notchedOutline': { borderColor: '#30363D' },
            '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: '#58A6FF' },
            minWidth: 90,
          }}
        >
          {INTERVALS.map((i) => (
            <MenuItem key={i.value} value={i.value} sx={{ fontSize: '0.8rem' }}>
              {i.label}
            </MenuItem>
          ))}
        </Select>

        <Tooltip title="Refresh all data now">
          <IconButton onClick={handleRefreshAll} sx={{ color: '#58A6FF' }}>
            <RefreshIcon />
          </IconButton>
        </Tooltip>
      </Toolbar>
    </AppBar>
  )
}
