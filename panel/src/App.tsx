const exampleCommand = 'docker exec codenetix codenetix fix <url-de-issue>'

export default function App() {
  return (
    <main>
      <h1>CodeNetix está en marcha</h1>
      <p>Para lanzar trabajo sobre una issue de GitHub, ejecuta:</p>
      <pre>
        <code>{exampleCommand}</code>
      </pre>
    </main>
  )
}
