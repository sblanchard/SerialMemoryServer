/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        primary: {
          DEFAULT: '#e94560',
          hover: '#ff6b6b',
        },
        bg: {
          primary: '#1a1a2e',
          secondary: '#16213e',
          tertiary: '#0f3460',
        },
        entity: {
          person: '#3b82f6',
          org: '#8b5cf6',
          gpe: '#10b981',
          date: '#f59e0b',
          email: '#06b6d4',
          url: '#ec4899',
          title: '#6366f1',
          default: '#e94560',
        }
      }
    },
  },
  plugins: [],
}
