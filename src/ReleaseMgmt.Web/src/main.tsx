import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '../../../docs/ui/tokens.css'
import './app.css'
import { applyTheme, loadTheme } from './theme'
import App from './App'

applyTheme(loadTheme())
createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
