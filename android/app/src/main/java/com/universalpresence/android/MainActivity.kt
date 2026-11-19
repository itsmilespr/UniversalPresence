package com.universalpresence.android

import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import android.provider.Settings
import android.widget.Button
import android.widget.EditText
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity

class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        val tokenBox = findViewById<EditText>(R.id.tokenBox)
        val prefs = getSharedPreferences("up", MODE_PRIVATE)
        tokenBox.setText(prefs.getString("token", ""))

        findViewById<Button>(R.id.saveButton).setOnClickListener {
            prefs.edit().putString("token", tokenBox.text.toString().trim()).apply()
            Toast.makeText(this, "Token guardado", Toast.LENGTH_SHORT).show()
        }

        findViewById<Button>(R.id.startButton).setOnClickListener {
            startForegroundService(Intent(this, PresenceService::class.java))
        }

        findViewById<Button>(R.id.stopButton).setOnClickListener {
            stopService(Intent(this, PresenceService::class.java))
        }

        findViewById<Button>(R.id.accessButton).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
    }
}