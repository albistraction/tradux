# ADR-0022: Runtime portátil (netstandard2.0) para o ERP

- **Status**: substituído
- **Substituído por**: ADR-0025 (o produto é só o microsserviço, 2026-11-16)
- **Data**: 2026-10-09
- **Fase**: 18
- **Revê**: ADR-0002 (alvo único net10.0) para os projetos do caminho de geração

## Contexto

O usuário definiu que o framework precisa servir o **ERP** (ERP
Documentos Eletrônicos 5.8.16). Na pergunta de integração, escolheu "os dois":
runtime dentro do ERP e API HTTP ao lado.

Fatos do ERP que pesam:
- **Plataforma:** .NET Framework 4.8 nos 19 projetos. Os projetos usam
  `.csproj` antigo, sem `LangVersion`, então compilam em C# 7.3.
- **Escala de NFS-e:** cerca de 300 municípios, 235 classes de provedor e 217
  conversores escritos à mão (`*ConversorHelper`, ~171 mil linhas) de
  `Rps`/`LoteRps` para as classes xsd.exe do provedor.
- **Entrada:** o `Rps` guarda a DPS nacional em XML, com infErp
  (`NfseCustomizadoBase.ObterDps`). É a entrada natural de um template
  DPS → provedor (ADR-0015).
- **O que já existe no ERP:** assinatura (`AssinaturaDigital`), envio
  SOAP/REST, consultas e leitura dos retornos.

Um assembly `net10.0` não pode ser referenciado por um projeto .NET
Framework 4.8.

## Decisão

1. **Alvos duplos no runtime.**
   - Os projetos do caminho de geração passam a compilar para
     `netstandard2.0;net10.0`: Domain, Xsd, Engine, Contracts, Application e
     Sdk.
   - Isso vem de `build/Portable.props`, importado por cada um.
   - API, Web, MongoDB, Knowledge e o conector de assinatura continuam
     `net10.0` (autoria e serviço).
2. **Compatibilidade sem mudar semântica.**
   - Só no alvo `netstandard2.0` entram:
     - PolySharp (atributos de linguagem: `init`, `required`,
       `Index`/`Range`, nulabilidade);
     - `Portable.System.DateTimeOnly`;
     - `System.Text.Json`;
     - `System.Diagnostics.DiagnosticSource`;
     - `Microsoft.Bcl.TimeProvider`.
   - O shim interno `build/Compat/NetStandardCompat.cs` reimplementa, com a
     mesma semântica, as APIs novas usadas (`ArgumentNullException.ThrowIfNull`,
     `DistinctBy`, `WaitAsync`, `KeyValuePair.Create` …). Usa membros de
     extensão do C# 14.
   - Onde não há equivalente direto (`GeneratedRegex`, `PeriodicTimer`, busca
     por `Span` no dicionário de entidades), um `#if NETSTANDARD2_0` mínimo
     mantém o comportamento.
   - Nos demais casos, o código passou a usar uma forma que vale nos dois
     alvos:
     - fatias de array → `Take`;
     - `TimeSpan / int` → `FromTicks`;
     - `int.Parse(span)` → `Substring`;
     - `IReadOnlySet<T>` → `IReadOnlyCollection<T>`;
     - `Lazy<T>(valor)` → `Lazy` já materializado.
3. **Conversor gerado: C# 12, qualquer alvo.**
   - O arquivo gerado usa C# 12, mas nenhuma API de runtime moderno. Compila
     e roda contra .NET Framework 4.8 desde que o projeto que o hospeda use
     `LangVersion` 12 ou `latest`. O cabeçalho do arquivo diz isso.
   - No ERP, os conversores ficam num projeto de formato SDK (`net48`,
     `LangVersion` 12) referenciado pelo `Processos` (Fase 19).
   - O gerador **não** foi empobrecido para C# 7.3. Isso custaria as anotações
     de nulabilidade e a exigência de implementar operador customizado em
     tempo de compilação.
4. **Gate em .NET Framework de verdade.**
   - `tests/Tradux.NetFramework.Tests` roda em `net48`. O teste:
     1. importa o XSD;
     2. compila o template;
     3. gera e valida o XML;
     4. gera o conversor;
     5. compila o conversor com Roslyn em C# 12 dentro do 4.8;
     6. confere que o resultado é igual ao do motor.
   - O teste de arquitetura falha se um projeto do runtime perder o alvo
     `netstandard2.0`.
5. **Timeout de regex dentro do ERP.**
   - A validação XSD do .NET avalia patterns com o timeout padrão do
     processo. No .NET Framework, esse timeout é o `REGEX_DEFAULT_MATCH_TIMEOUT`
     do AppDomain, lido **uma vez**, na primeira `Regex` criada.
   - O host do ERP precisa chamar `RegexTimeout.Configure` no início
     (`Program`/`MainService`), antes de qualquer `Regex`. O valor vale para o
     AppDomain inteiro do ERP, regexes do próprio ERP inclusive. Por isso a
     recomendação é um valor folgado (ex.: 2 s, via `TRADUX_REGEX_TIMEOUT_MS`),
     que só corta casos patológicos.
   - Sem isso, as defesas que continuam valendo são:
     - o `PatternSafetyChecker` na importação, que recusa pattern de
       backtracking catastrófico;
     - o `PatternGuard`, que testa cada valor gerado com timeout próprio
       antes da validação.
   - O que fica sem prazo é só a validação XSD de um pattern que passou pelo
     checker. Essa lacuna é conhecida e fica registrada aqui.
6. **API HTTP.** Continua `net10.0`, para quem preferir o serviço ao lado (ex.:
   ErpCloud). Contrato inalterado (`POST …/xml`, `POST …/transform`).

## Alternativas rejeitadas

- **Só a API HTTP:** cada instalação do ERP (on-premise) precisaria de um
  serviço .NET 10 e de um MongoDB a mais, e a emissão dependeria deles.
- **Gerar o conversor em C# 7.3:** empobrece o código gerado para todos os
  consumidores. Um projeto de formato SDK com C# 12 é suportado pelo MSBuild
  atual que compila o ERP.
- **Biblioteca separada "lite" para o ERP:** duplicaria o motor. Os alvos
  duplos mantêm uma única base de código.

## Consequências

- O runtime tem dois alvos. A CI compila e testa os dois, e o teste de
  arquitetura protege o alvo `netstandard2.0`.
- Código novo no runtime precisa compilar em `netstandard2.0`. API nova do
  .NET entra pelo shim, ou com `#if` mínimo, sem mudar a semântica.
- O conector de assinatura continua `net10.0`. O ERP assina com o próprio
  `AssinaturaDigital`.
