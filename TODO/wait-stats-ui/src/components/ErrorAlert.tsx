import { Alert, Box, Button, Typography } from '@mui/material'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import RefreshIcon from '@mui/icons-material/Refresh'

interface ErrorAlertProps {
  message?: string
  onRetry?: () => void
}

export default function ErrorAlert({
  message = 'Failed to load data. Is the API running?',
  onRetry,
}: ErrorAlertProps) {
  return (
    <Box py={4} display="flex" justifyContent="center">
      <Alert
        severity="error"
        icon={<ErrorOutlineIcon />}
        sx={{ maxWidth: 560, width: '100%', bgcolor: '#FF6B6B11', border: '1px solid #FF6B6B44' }}
        action={
          onRetry && (
            <Button
              size="small"
              startIcon={<RefreshIcon />}
              onClick={onRetry}
              sx={{ color: '#FF6B6B', textTransform: 'none' }}
            >
              Retry
            </Button>
          )
        }
      >
        <Typography variant="body2">{message}</Typography>
      </Alert>
    </Box>
  )
}
