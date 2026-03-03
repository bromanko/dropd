.PHONY: dev spike apple-auth

dev:
	overmind start

spike:
	dotnet run --project spikes/api-exploration

apple-auth:
	@echo "Opening http://localhost:8070 — authorize with your Apple ID"
	@python3 -m http.server 8070 --directory spikes/api-exploration --bind 127.0.0.1
