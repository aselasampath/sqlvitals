import { Chip } from '@mui/material'

interface StatusBadgeProps {
  label: string
  color?: string
  size?: 'small' | 'medium'
  variant?: 'filled' | 'outlined'
}

export default function StatusBadge({
  label,
  color = '#58A6FF',
  size = 'small',
  variant = 'filled',
}: StatusBadgeProps) {
  if (variant === 'outlined') {
    return (
      <Chip
        label={label}
        size={size}
        variant="outlined"
        sx={{ borderColor: color, color, fontWeight: 600, fontSize: '0.72rem' }}
      />
    )
  }

  return (
    <Chip
      label={label}
      size={size}
      sx={{
        backgroundColor: `${color}22`,
        color,
        fontWeight: 600,
        fontSize: '0.72rem',
        border: `1px solid ${color}55`,
      }}
    />
  )
}
