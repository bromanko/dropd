.PHONY: build test test-dd clean dev spike apple-auth sync smoke

build:
	dotnet build packages/dropd/dropd.sln

test:
	dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

test-dd:
	dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter-test-case "DD-"

clean:
	dotnet clean packages/dropd/dropd.sln

dev:
	overmind start

spike:
	dotnet run --project spikes/api-exploration

apple-auth:
	@echo "Opening http://localhost:8070 — authorize with your Apple ID"
	@python3 -m http.server 8070 --directory spikes/api-exploration --bind 127.0.0.1

sync:
	dotnet run --project spikes/integration-smoke

smoke:
	@echo "'make smoke' is deprecated; running full sync via 'make sync'"
	@$(MAKE) sync
