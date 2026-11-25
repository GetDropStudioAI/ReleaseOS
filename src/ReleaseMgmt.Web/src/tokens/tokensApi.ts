import { get, post } from '../api.ts'
import type { ApiToken, TokenList, TokenWithSecret } from './tokensLogic.ts'

/** API tokens (REOS-162, Policies.Admin: RTE and Release Manager). Make and rotate answer with the secret once; nothing here keeps it. */
export const listTokens = () => get<TokenList>('/api/v1/api-tokens')
export const makeToken = (b: { name: string; scopes: string[]; expiresAt: string | null }) => post<TokenWithSecret>('/api/v1/api-tokens', b)
export const rotateToken = (id: string, b: { expiresAt?: string }, version: number) => post<TokenWithSecret>(`/api/v1/api-tokens/${id}:rotate`, b, version)
export const revokeToken = (id: string, version: number) => post<ApiToken>(`/api/v1/api-tokens/${id}:revoke`, {}, version)
