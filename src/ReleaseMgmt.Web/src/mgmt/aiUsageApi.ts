import { get } from '../api.ts'
import type { AiLedgerEntry, AiUsage } from './aiUsageLogic.ts'

/** REOS-185: the AI allowance and the usage ledger, for an RTE or Release Manager. There is no call that adds credits: packs come from billing. */
export const getAiUsage = () => get<AiUsage>('/api/v1/ai/usage')
export const getAiLedger = (limit = 25) => get<AiLedgerEntry[]>(`/api/v1/ai/usage/ledger?limit=${limit}`)
