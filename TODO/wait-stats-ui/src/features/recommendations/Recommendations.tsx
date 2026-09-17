import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Box,
  Chip,
  Grid,
  Link,
  Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { useState } from 'react'
import { useRecommendations } from '../../hooks/useWaitStats'
import LoadingSpinner from '../../components/LoadingSpinner'
import ErrorAlert from '../../components/ErrorAlert'
import StatusBadge from '../../components/StatusBadge'
import type { RefreshInterval } from '../../types/waitStats'

interface Props { interval: RefreshInterval }

const SEVERITY_ORDER: Record<string, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Info: 4 }

export default function Recommendations({ interval }: Props) {
  const { data, isLoading, isError, error, refetch } = useRecommendations(interval)
  const [filterSev, setFilterSev] = useState<string>('All')

  if (isLoading) return <LoadingSpinner message="Analyzing wait patterns..." />
  if (isError) return <ErrorAlert message={(error as Error).message} onRetry={() => refetch()} />

  const sorted = [...data!].sort(
    (a, b) => (SEVERITY_ORDER[a.severity] ?? 9) - (SEVERITY_ORDER[b.severity] ?? 9)
  )

  const severities = ['All', ...Array.from(new Set(sorted.map((r) => r.severity)))]
  const filtered = filterSev === 'All' ? sorted : sorted.filter((r) => r.severity === filterSev)

  const counts = {
    Critical: sorted.filter((r) => r.severity === 'Critical').length,
    High: sorted.filter((r) => r.severity === 'High').length,
    Medium: sorted.filter((r) => r.severity === 'Medium').length,
  }

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} mb={0.5}>Recommendations</Typography>
      <Typography variant="body2" color="text.secondary" mb={2}>
        Actionable insights based on current wait patterns — {data!.length} recommendations
      </Typography>

      {/* Summary */}
      <Grid container spacing={2} mb={2}>
        {([['Critical', '#FF6B6B'], ['High', '#FFA62B'], ['Medium', '#FFC107']] as [string, string][]).map(([sev, color]) => (
          <Grid item key={sev}>
            <Box
              sx={{
                px: 3, py: 1.5,
                bgcolor: `${color}11`,
                border: `1px solid ${color}44`,
                borderRadius: 2,
                textAlign: 'center',
                cursor: 'pointer',
                '&:hover': { bgcolor: `${color}22` },
              }}
              onClick={() => setFilterSev(sev === filterSev ? 'All' : sev)}
            >
              <Typography variant="h5" fontWeight={700} sx={{ color }}>
                {counts[sev as keyof typeof counts]}
              </Typography>
              <Typography variant="caption" color="text.secondary">{sev}</Typography>
            </Box>
          </Grid>
        ))}
      </Grid>

      {/* Severity filter */}
      <Box display="flex" gap={1} flexWrap="wrap" mb={2}>
        {severities.map((sev) => {
          const recColor = data!.find((r) => r.severity === sev)?.severityColor ?? '#58A6FF'
          return (
            <Chip
              key={sev}
              label={sev}
              size="small"
              onClick={() => setFilterSev(sev)}
              sx={{
                cursor: 'pointer',
                border: `1px solid ${sev === 'All' ? '#58A6FF' : recColor}`,
                bgcolor: filterSev === sev ? `${sev === 'All' ? '#58A6FF' : recColor}22` : 'transparent',
                color: sev === 'All' ? '#58A6FF' : recColor,
                fontWeight: filterSev === sev ? 700 : 500,
              }}
            />
          )
        })}
      </Box>

      {filtered.length === 0 ? (
        <Box py={6} textAlign="center">
          <Typography color="text.secondary">No recommendations for this severity level</Typography>
        </Box>
      ) : (
        filtered.map((rec, i) => (
          <Accordion
            key={i}
            disableGutters
            sx={{
              mb: 1,
              bgcolor: '#161B22',
              border: `1px solid ${rec.severityColor}33`,
              '&:before': { display: 'none' },
              '&.Mui-expanded': { borderColor: rec.severityColor },
            }}
          >
            <AccordionSummary expandIcon={<ExpandMoreIcon sx={{ color: '#8B949E' }} />}>
              <Box display="flex" alignItems="center" gap={1.5} flexWrap="wrap" width="100%">
                <Typography sx={{ fontSize: '1.1rem' }}>{rec.severityIcon}</Typography>
                <Chip
                  label={rec.severity}
                  size="small"
                  sx={{ bgcolor: `${rec.severityColor}22`, color: rec.severityColor, border: `1px solid ${rec.severityColor}55`, fontSize: '0.7rem', fontWeight: 700, minWidth: 68 }}
                />
                <Typography variant="subtitle2" fontWeight={600} sx={{ flexGrow: 1 }}>
                  {rec.title}
                </Typography>
                <Box display="flex" gap={1} alignItems="center">
                  <StatusBadge label={rec.category} color="#58A6FF" variant="outlined" />
                  {rec.waitType && (
                    <Typography variant="caption" sx={{ fontFamily: 'monospace', color: '#8B949E', fontSize: '0.7rem' }}>
                      {rec.waitType}
                    </Typography>
                  )}
                </Box>
              </Box>
            </AccordionSummary>
            <AccordionDetails sx={{ pt: 0 }}>
              <Grid container spacing={2}>
                <Grid item xs={12} md={8}>
                  <Typography variant="body2" color="text.secondary" mb={1}>{rec.description}</Typography>
                  <Box sx={{ bgcolor: '#0D1117', borderRadius: 1, p: 1.5, border: '1px solid #30363D', mb: 1 }}>
                    <Typography variant="caption" color="text.secondary" display="block" fontWeight={600} mb={0.5}>RECOMMENDED ACTION</Typography>
                    <Typography variant="body2">{rec.action}</Typography>
                  </Box>
                </Grid>
                <Grid item xs={12} md={4}>
                  {rec.metricValue && (
                    <Box sx={{ bgcolor: '#0D1117', borderRadius: 1, p: 1.5, border: '1px solid #30363D', mb: 1 }}>
                      <Typography variant="caption" color="text.secondary" display="block" fontWeight={600} mb={0.5}>METRIC</Typography>
                      <Typography variant="body2">Value: <strong style={{ color: rec.severityColor }}>{rec.metricValue}</strong></Typography>
                      {rec.threshold && <Typography variant="caption" color="text.secondary">Threshold: {rec.threshold}</Typography>}
                      {rec.metricVsThreshold && <Typography variant="caption" color="text.secondary" display="block">{rec.metricVsThreshold}</Typography>}
                    </Box>
                  )}
                  {rec.learnMoreUrl && (
                    <Link href={rec.learnMoreUrl} target="_blank" rel="noopener" sx={{ fontSize: '0.78rem', display: 'flex', alignItems: 'center', gap: 0.5 }}>
                      Learn more <OpenInNewIcon sx={{ fontSize: 12 }} />
                    </Link>
                  )}
                </Grid>
              </Grid>
            </AccordionDetails>
          </Accordion>
        ))
      )}
    </Box>
  )
}
