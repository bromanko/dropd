.PHONY: dev spike

dev:
	overmind start

spike:
	dotnet run --project spikes/api-exploration
