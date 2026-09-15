## Session Start Checklist

On EVERY session start (including resumed/compacted sessions), before
responding to any user message:

1. Check if the dev server is running (`lsof -i :3000 -i :5173 -i :8080 2>/dev/null` or `pgrep -f "npm run dev"`)
2. If not running, check memory for the start command and execute it
3. If no memory exists, detect from project files (package.json, docker-compose.yml, etc.)
4. Only then proceed with the user's request
