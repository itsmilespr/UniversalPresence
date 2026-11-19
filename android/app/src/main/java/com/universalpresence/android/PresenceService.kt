package com.universalpresence.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.IBinder
import android.os.Looper
import android.media.MediaMetadata
import android.os.Handler
import com.my.kizzyrpc.KizzyRPC
import com.my.kizzyrpc.model.Activity
import com.my.kizzyrpc.model.Assets
import com.my.kizzyrpc.model.Timestamps

class PresenceService : Service() {
    private var rpc: KizzyRPC? = null
    private var mediaManager: MediaSessionManager? = null
    private val handler = Handler(Looper.getMainLooper())
    private val channelId = "up_presence"

    override fun onCreate() {
        super.onCreate()
        startForeground(1, buildNotification("Iniciando..."))
        val token = getSharedPreferences("up", MODE_PRIVATE).getString("token", "") ?: ""
        if (token.isNotBlank()) {
            rpc = KizzyRPC(token)
        }
        mediaManager = getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager
        try {
            mediaManager?.addOnActiveSessionsChangedListener({ sessions ->
                handler.post { updatePresence(sessions) }
            }, ComponentName(this, MediaSessionListener::class.java))
            val sessions = mediaManager?.getActiveSessions(ComponentName(this, MediaSessionListener::class.java))
            updatePresence(sessions)
        } catch (e: SecurityException) {
            updateNotification("Activa el acceso a notificaciones")
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun updatePresence(sessions: List<MediaController>?) {
        val session = sessions?.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
            ?: sessions?.firstOrNull()
        if (session == null) {
            rpc?.closeRPC()
            updateNotification("Sin reproducción")
            return
        }
        val meta = session.metadata
        val title = meta?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: ""
        val artist = meta?.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: ""
        val album = meta?.getString(MediaMetadata.METADATA_KEY_ALBUM) ?: ""
        val state = session.playbackState
        val playing = state?.state == PlaybackState.STATE_PLAYING
        val position = state?.position ?: 0L
        val duration = meta?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0L
        val now = System.currentTimeMillis()
        val start = if (playing && position > 0) now - position else null
        val end = if (playing && duration > 0) now + (duration - position) else null

        val activity = Activity(
            name = "Universal Presence",
            state = artist.ifBlank { album },
            details = title.ifBlank { "Reproduciendo" },
            type = 2,
            timestamps = if (start != null) Timestamps(start = start, end = end) else null,
            assets = Assets(
                largeImage = "",
                smallImage = "",
                largeText = album.ifBlank { artist },
                smallText = ""
            ),
            buttons = emptyList(),
            metadata = com.my.kizzyrpc.model.Metadata(emptyList()),
            applicationId = "1558323929766953042"
        )
        rpc?.setActivity(activity, "online", 0)
        updateNotification(if (title.isNotBlank()) "$title — $artist" else "Presencia activa")
    }

    private fun buildNotification(text: String): Notification {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(NotificationManager::class.java)
            val channel = NotificationChannel(channelId, "Universal Presence", NotificationManager.IMPORTANCE_LOW)
            nm.createNotificationChannel(channel)
        }
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, channelId)
                .setContentTitle("Universal Presence")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .build()
        } else {
            Notification.Builder(this)
                .setContentTitle("Universal Presence")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .build()
        }
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(1, buildNotification(text))
    }

    override fun onDestroy() {
        super.onDestroy()
        rpc?.closeRPC()
    }
}