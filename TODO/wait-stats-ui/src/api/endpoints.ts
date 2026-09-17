import apiClient from './apiClient'
import type {
  WaitStatCumulative,
  ActiveWait,
  WaitCategorySummary,
  SignalVsResourceWait,
  TopWaitType,
  Recommendation,
  ServerHealthKpi,
} from '../types/waitStats'

export const fetchCumulativeWaits = async (): Promise<WaitStatCumulative[]> => {
  const { data } = await apiClient.get<WaitStatCumulative[]>('/api/waitstats/cumulative')
  return data
}

export const fetchActiveWaits = async (): Promise<ActiveWait[]> => {
  const { data } = await apiClient.get<ActiveWait[]>('/api/waitstats/active')
  return data
}

export const fetchWaitCategories = async (): Promise<WaitCategorySummary[]> => {
  const { data } = await apiClient.get<WaitCategorySummary[]>('/api/waitstats/categories')
  return data
}

export const fetchSignalVsResource = async (): Promise<SignalVsResourceWait> => {
  const { data } = await apiClient.get<SignalVsResourceWait>('/api/waitstats/signal-resource')
  return data
}

export const fetchTopWaitTypes = async (): Promise<TopWaitType[]> => {
  const { data } = await apiClient.get<TopWaitType[]>('/api/waitstats/top')
  return data
}

export const fetchRecommendations = async (): Promise<Recommendation[]> => {
  const { data } = await apiClient.get<Recommendation[]>('/api/waitstats/recommendations')
  return data
}

export const fetchServerHealthKpi = async (): Promise<ServerHealthKpi> => {
  const { data } = await apiClient.get<ServerHealthKpi>('/api/waitstats/health')
  return data
}
