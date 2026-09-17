import { Card, CardContent, Typography, Box, Skeleton, Tooltip } from '@mui/material'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import TrendingUpIcon from '@mui/icons-material/TrendingUp'
import TrendingDownIcon from '@mui/icons-material/TrendingDown'

interface KpiCardProps {
  title: string
  value: string | number
  subtitle?: string
  color?: string
  loading?: boolean
  trend?: 'up' | 'down' | 'neutral'
  trendLabel?: string
  tooltip?: string
  icon?: React.ReactNode
}

export default function KpiCard({
  title,
  value,
  subtitle,
  color = '#58A6FF',
  loading = false,
  trend,
  trendLabel,
  tooltip,
  icon,
}: KpiCardProps) {
  const trendColor =
    trend === 'up' ? '#FF6B6B' : trend === 'down' ? '#26E7A6' : '#8B949E'

  return (
    <Card sx={{ height: '100%', position: 'relative' }}>
      <CardContent sx={{ p: 2.5 }}>
        <Box display="flex" alignItems="center" justifyContent="space-between" mb={1}>
          <Typography variant="caption" color="text.secondary" fontWeight={600} sx={{ textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            {title}
          </Typography>
          <Box display="flex" alignItems="center" gap={0.5}>
            {icon}
            {tooltip && (
              <Tooltip title={tooltip} arrow>
                <InfoOutlinedIcon sx={{ fontSize: 14, color: 'text.secondary', cursor: 'help' }} />
              </Tooltip>
            )}
          </Box>
        </Box>

        {loading ? (
          <>
            <Skeleton variant="text" width="60%" height={44} />
            <Skeleton variant="text" width="40%" height={20} />
          </>
        ) : (
          <>
            <Typography
              variant="h4"
              fontWeight={700}
              sx={{ color, lineHeight: 1.2, mb: 0.5, fontSize: { xs: '1.6rem', lg: '2rem' } }}
            >
              {value}
            </Typography>

            {(subtitle || trendLabel) && (
              <Box display="flex" alignItems="center" gap={0.5}>
                {trend && trend !== 'neutral' && (
                  trend === 'up'
                    ? <TrendingUpIcon sx={{ fontSize: 14, color: trendColor }} />
                    : <TrendingDownIcon sx={{ fontSize: 14, color: trendColor }} />
                )}
                <Typography variant="caption" sx={{ color: trendLabel ? trendColor : 'text.secondary' }}>
                  {trendLabel ?? subtitle}
                </Typography>
              </Box>
            )}
          </>
        )}
      </CardContent>
      <Box
        sx={{
          position: 'absolute',
          bottom: 0,
          left: 0,
          right: 0,
          height: 3,
          backgroundColor: color,
          borderRadius: '0 0 8px 8px',
          opacity: 0.7,
        }}
      />
    </Card>
  )
}
