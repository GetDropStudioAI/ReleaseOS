import { get, post } from '../api.ts'
import type { AiAction, AiStatus } from './aiLogic.ts'

/** REOS-183: the AI platform's admin status and the workspace controls. For an RTE or Release Manager. The key is not handled here at all. */
export const getAiStatus = () => get<AiStatus>('/api/v1/ai/status')
export const aiAction = (a: AiAction) => post<AiStatus>(`/api/v1/ai/workspace:${a}`, {})
