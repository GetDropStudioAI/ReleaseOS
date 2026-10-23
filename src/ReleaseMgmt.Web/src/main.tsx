import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '../../../docs/ui/tokens.css'
import './app.css'
import './ui/ui.css'
import { applyTheme, loadTheme } from './theme'
import App from './App'
import { installWorkspaceHeader } from './workspace'

applyTheme(loadTheme())
installWorkspaceHeader()   // REOS-106: every /api/ call names the workspace the person chose
createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
