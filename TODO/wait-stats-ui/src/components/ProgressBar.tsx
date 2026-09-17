import { Box, LinearProgress, Tooltip, Typography } from '@mui/material'

interface ProgressBarProps {
  value: number          // 0-100
  max?: number
  color?: string
  showLabel?: boolean
  tooltip?: string
  height?: number
  label?: string
}

export default function ProgressBar({
  value,
  color = '#58A6FF',
  showLabel = true,
  tooltip,
  height = 6,
  label,
}: ProgressBarProps) {
  const clamped = Math.min(100, Math.max(0, value))

  const bar = (
    <Box sx={{ width: '100%' }}>
      {label && (
        <Box display="flex" justifyContent="space-between" mb={0.25}>
          <Typography variant="caption" color="text.secondary">
            {label}
          </Typography>
          {showLabel && (
            <Typography variant="caption" sx={{ color, fontWeight: 600 }}>
              {clamped.toFixed(1)}%
            </Typography>
          )}
        </Box>
      )}
      <Box sx={{ position: 'relative', height, bgcolor: '#21262D', borderRadius: height / 2, overflow: 'hidden' }}>
        <Box
          sx={{
            position: 'absolute',
            left: 0,
            top: 0,
            bottom: 0,
            width: `${clamped}%`,
            backgroundColor: color,
            borderRadius: height / 2,
            transition: 'width 0.4s ease',
          }}
        />
      </Box>
      {!label && showLabel && (
        <Typography variant="caption" sx={{ color, fontWeight: 600 }}>
          {clamped.toFixed(1)}%
        </Typography>
      )}
    </Box>
  )

  if (tooltip) {
    return <Tooltip title={tooltip} arrow>{bar}</Tooltip>
  }

  return bar
}

export { LinearProgress }
