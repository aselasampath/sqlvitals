// ─── Cumulative Waits ───────────────────────────────────────────────────────
export interface WaitStatCumulative {
  waitType: string
  waitCategory: string
  isBenign: number
  waitingTasksCount: number
  waitTimeMs: number
  maxWaitTimeMs: number
  signalWaitTimeMs: number
  resourceWaitTimeMs: number
  avgWaitTimeMs: number
  signalWaitPct: number
  pctOfTotalWaits: number
  waitTimeSec: number
  captureTime: string
  serverStartTime: string
  categoryColor: string
  severityBand: string
}

// ─── Active Waits ────────────────────────────────────────────────────────────
export interface ActiveWait {
  sessionId: number
  requestId: number
  waitType: string
  waitTimeMs: number
  waitTimeSec: number
  blockedBy: number | null
  status: string
  command: string
  databaseName: string
  loginName: string
  hostName: string
  programName: string
  cpuTime: number
  logicalReads: number
  reads: number
  writes: number
  rowCount: number
  openTransactionCount: number
  transactionIsolationLevel: string
  sqlText: string | null
  waitCategory: string
  waitCategoryColor: string
  isBlocked: boolean
  blockingChainDepth: number
  elapsedMs: number
}

// ─── Wait Category Summary ────────────────────────────────────────────────────
export interface WaitCategorySummary {
  waitCategory: string
  totalWaitMs: number
  totalSignalWaitMs: number
  totalResourceWaitMs: number
  totalWaitingTasks: number
  maxSingleWaitMs: number
  uniqueWaitTypes: number
  totalWaitSec: number
  totalWaitMin: number
  signalWaitPct: number
  resourceWaitPct: number
  pctOfTotalWaits: number
  avgWaitMsPerTask: number
  captureTime: string
  categoryColor: string
  sortOrder: number
}

// ─── Signal vs Resource ───────────────────────────────────────────────────────
export interface SignalVsResourceWait {
  totalWaitMs: number
  totalSignalMs: number
  totalResourceMs: number
  serverSignalWaitPct: number
  serverResourceWaitPct: number
  cpuPressureLevel: string
  cpuPressureDescription: string
  totalWaitSec: number
  totalSignalSec: number
  totalResourceSec: number
  serverStartTime: string
  captureTime: string
}

// ─── Top Wait Types ───────────────────────────────────────────────────────
export interface TopWaitType {
  waitRank: number
  rankLabel: string
  waitType: string
  waitCategory: string
  waitingTasksCount: number
  waitTimeMs: number
  maxWaitTimeMs: number
  signalWaitTimeMs: number
  resourceWaitTimeMs: number
  signalWaitPct: number
  waitTimeSec: number
  avgWaitMsPerTask: number
  pctOfTotal: number
  cumulativePct: number
  captureTime: string
  categoryColor: string
}

// ─── Recommendations ─────────────────────────────────────────────────────────
export interface Recommendation {
  recommendationId: string
  severity: string
  severityColor: string
  severityIcon: string
  category: string
  title: string
  description: string
  action: string
  metricValue: string
  threshold: string
  metricVsThreshold: string
  captureTime: string
  // optional fields that may be absent
  waitType?: string
  impact?: string
  metricName?: string
  learnMoreUrl?: string
}

// ─── Server Health KPIs ───────────────────────────────────────────────────────
export interface ServerHealthKpi {
  serverStartTime: string
  captureTime: string
  uptimeHours: number
  uptimeDays: number
  logicalCPUs: number
  hyperthreadRatio: number
  physicalMemoryMB: number
  virtualMemoryMB: number
  committedMemMB: number
  targetMemMB: number
  activeUserSessions: number
  blockedRequests: number
  runningRequests: number
  waitingRequests: number
  globalSignalWaitPct: number
  bufferCacheHitRatio: number
  pageLifeExpectancy: number
  batchRequestsPerSec: number
  cpuPressureStatus: string
  cpuStatusColor: string
  memoryUtilizationPct: number
  pleStatus: string
}

// ─── Utility ─────────────────────────────────────────────────────────────────
export type RefreshInterval = 10 | 30 | 60 | 300 | 0  // 0 = manual
