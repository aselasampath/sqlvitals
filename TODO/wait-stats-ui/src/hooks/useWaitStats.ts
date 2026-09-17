import { useQuery } from '@tanstack/react-query'
import {
  fetchCumulativeWaits,
  fetchActiveWaits,
  fetchWaitCategories,
  fetchSignalVsResource,
  fetchTopWaitTypes,
  fetchRecommendations,
  fetchServerHealthKpi,
} from '../api/endpoints'
import type { RefreshInterval } from '../types/waitStats'

const staleMap: Record<RefreshInterval, number> = {
  10: 8_000,
  30: 25_000,
  60: 55_000,
  300: 290_000,
  0: Infinity,
}

function queryOpts(interval: RefreshInterval) {
  return {
    staleTime: staleMap[interval],
    refetchInterval: interval === 0 ? false as const : interval * 1000,
    retry: 2,
  }
}

export function useCumulativeWaits(interval: RefreshInterval = 30) {
  return useQuery({
    queryKey: ['cumulativeWaits'],
    queryFn: fetchCumulativeWaits,
    ...queryOpts(interval),
  })
}

export function useActiveWaits(interval: RefreshInterval = 10) {
  return useQuery({
    queryKey: ['activeWaits'],
    queryFn: fetchActiveWaits,
    ...queryOpts(interval),
  })
}

export function useWaitCategories(interval: RefreshInterval = 30) {
  return useQuery({
    queryKey: ['waitCategories'],
    queryFn: fetchWaitCategories,
    ...queryOpts(interval),
  })
}

export function useSignalVsResource(interval: RefreshInterval = 60) {
  return useQuery({
    queryKey: ['signalVsResource'],
    queryFn: fetchSignalVsResource,
    ...queryOpts(interval),
  })
}

export function useTopWaitTypes(interval: RefreshInterval = 30) {
  return useQuery({
    queryKey: ['topWaitTypes'],
    queryFn: fetchTopWaitTypes,
    ...queryOpts(interval),
  })
}

export function useRecommendations(interval: RefreshInterval = 60) {
  return useQuery({
    queryKey: ['recommendations'],
    queryFn: fetchRecommendations,
    ...queryOpts(interval),
  })
}

export function useServerHealthKpi(interval: RefreshInterval = 10) {
  return useQuery({
    queryKey: ['serverHealthKpi'],
    queryFn: fetchServerHealthKpi,
    ...queryOpts(interval),
  })
}
