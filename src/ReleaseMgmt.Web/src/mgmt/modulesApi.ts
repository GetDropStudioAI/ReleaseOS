import { get, post } from '../api.ts'
import type { ModuleRow } from './modulesLogic.ts'

/** Add-on modules of the current workspace (REOS-174). Anyone signed in reads; enabling and disabling are for an RTE or Release Manager. */
export const listModules = () => get<ModuleRow[]>('/api/v1/modules')
export const enableModule = (key: string, version: number) => post<ModuleRow>(`/api/v1/modules/${encodeURIComponent(key)}:enable`, {}, version)
export const disableModule = (key: string, version: number) => post<ModuleRow>(`/api/v1/modules/${encodeURIComponent(key)}:disable`, {}, version)

/**
 * A change made in this tab reaches the navigation at once, without waiting for the live push (which every session of the workspace gets, this one included).
 * The shell listens here and on the live connection.
 */
const listeners = new Set<() => void>()
export const onModulesChanged = (fn: () => void): (() => void) => { listeners.add(fn); return () => { listeners.delete(fn) } }
export const notifyModulesChanged = () => listeners.forEach(fn => fn())
